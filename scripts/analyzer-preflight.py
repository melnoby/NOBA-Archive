#!/usr/bin/env python3
"""
Catches, on Linux, the analyzer rules that otherwise only fire on the Windows build.

The WinUI project cannot be compiled here, so CA1822 (member does not access instance data), CA1859
(return the concrete type) and x:Name/type shadowing are found only by the Windows build — which spends
a whole round trip on a mistake that is mechanically obvious from the source.

This has now paid for itself four times: BuildFinding (CA1822 + CA1859), two x:Name fields that shadowed
Core types, BuildRow (CA1859), and BuildExtractRequest (CA1822). That last one also exposed a hole in
this script: it used to scan only the code-behind files, and BuildExtractRequest lives in Services/.
The scope is now the whole app project, because that is the scope the analyzer has.

Run from the repository root:  python3 scripts/analyzer-preflight.py
"""

import glob
import os
import re
import sys

APP = 'src/NOBA.Archive.App'

# Types the code-behind names, which an x:Name field of the same name would shadow inside its class.
SHADOWABLE = {
    'OperationState', 'OperationType', 'OperationId', 'OperationProgress', 'OperationSnapshot',
    'Application', 'Windows', 'FlowDirection', 'Grid', 'Button', 'Border', 'TextBlock', 'Style',
}

# Return types that are almost always wider than what a factory actually builds.
ABSTRACT_RETURNS = ('FrameworkElement', 'UIElement', 'DependencyObject', 'object')

# Members every UserControl/Window has, which count as instance state when referenced.
FRAMEWORK_MEMBERS = {
    'this', 'DispatcherQueue', 'XamlRoot', 'ActualTheme', 'Content', 'AppWindow',
    'InitializeComponent', 'Resources', 'Visibility', 'IsEnabled',
}


def methods(src):
    """Yield (name, is_static, body, line) for each method with a brace body."""
    pattern = re.compile(
        r'\n    (private|public|internal|protected)\s+(static\s+)?'
        r'(?:async\s+)?[\w\s<>?\[\],.]*?\s(\w+)\s*\([^)]*\)\s*\n?\s*\{')
    for m in pattern.finditer(src):
        i = src.index('{', m.end() - 1)
        depth, j = 0, i
        while j < len(src):
            if src[j] == '{':
                depth += 1
            elif src[j] == '}':
                depth -= 1
                if depth == 0:
                    break
            j += 1
        yield m.group(3), bool(m.group(2)), src[i:j], src[:m.start()].count('\n') + 2, m.group(1)


def instance_state(src, xaml_names):
    """Everything a reference to which proves a method needs an instance."""
    fields = set(re.findall(r'private\s+(?:readonly\s+)?[\w<>?\[\]]+\s+(_\w+)', src))
    # Properties, including expression-bodied ones, which have no brace body of their own.
    props = set(re.findall(
        r'\n    (?:private|public|internal|protected)\s+(?!static)[\w<>?\[\],.]+\s+(\w+)\s*(?:=>|\{\s*get)', src))
    instance_methods = {n for n, is_static, _, _, _ in methods(src) if not is_static}
    return fields | props | instance_methods | xaml_names | FRAMEWORK_MEMBERS


def check_file(cs_path, xaml_names, type_name):
    src = open(cs_path, encoding='utf-8').read()
    touch = instance_state(src, xaml_names)
    problems = 0

    for name, is_static, body, line, access in methods(src):
        # CA1822 analyses non-public members by default. A public method is usually an interface
        # implementation, which cannot be static however little state it touches - flagging those
        # would train the reader to ignore this script, which is worse than not having it.
        if is_static or name == type_name or access in ('public', 'protected'):
            continue
        if not any(re.search(rf'\b{re.escape(t)}\b', body) for t in touch - {name}):
            print(f"  CA1822  {cs_path}:{line}  '{name}' touches no instance state -> must be static")
            problems += 1

    for m in re.finditer(
            rf'private\s+(?:static\s+)?({"|".join(ABSTRACT_RETURNS)})\s+(\w+)\s*\(', src):
        print(f"  CA1859  {cs_path}  '{m.group(2)}' returns abstract '{m.group(1)}'")
        problems += 1

    return problems


def strip_noise(src):
    """Remove comments and string literals, so a type named in prose is not read as a reference."""
    src = re.sub(r'/\*.*?\*/', ' ', src, flags=re.S)
    src = re.sub(r'//[^\n]*', ' ', src)
    src = re.sub(r'@"(?:[^"]|"")*"', '""', src)
    src = re.sub(r'"(?:\\.|[^"\\])*"', '""', src)
    return src


def declared_types(root='src'):
    """typeName -> set of namespaces, over every project the app can reference."""
    owned = {}
    for cs in glob.glob(f'{root}/**/*.cs', recursive=True):
        if '/obj/' in cs or '/bin/' in cs:
            continue
        src = strip_noise(open(cs, encoding='utf-8').read())
        ns = re.search(r'\bnamespace\s+([\w.]+)', src)
        if not ns:
            continue
        for m in re.finditer(
                r'\b(?:public|internal)\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+|ref\s+)*'
                r'(?:class|record|struct|interface|enum|delegate)\s+(\w+)', src):
            owned.setdefault(m.group(1), set()).add(ns.group(1))
    return owned


def visible_namespaces(src):
    """Namespaces a file can resolve simple type names from: its usings, plus enclosing namespaces.

    A file in NOBA.Archive.App sees NOBA.Archive.App, NOBA.Archive, NOBA - never a child namespace
    such as NOBA.Archive.Core.Operations. That asymmetry is the whole reason this check exists.
    """
    seen = set(re.findall(r'^\s*(?:global\s+)?using\s+(?!static\b)([\w.]+)\s*;', src, flags=re.M))
    seen |= set(re.findall(r'^\s*(?:global\s+)?using\s+static\s+([\w.]+)\s*;', src, flags=re.M))
    own = re.search(r'\bnamespace\s+([\w.]+)', src)
    if own:
        parts = own.group(1).split('.')
        seen |= {'.'.join(parts[:i]) for i in range(1, len(parts) + 1)}
    return seen


def aliases(src):
    return set(re.findall(r'^\s*(?:global\s+)?using\s+(\w+)\s*=', src, flags=re.M))


def type_positions(body, name):
    """Where `name` is used as a TYPE, not as a member or a property being assigned.

    `block.FlowDirection = ...` names a property; `OperationState.Running` names a type. Only the
    second needs a using, and conflating them produced three false reports the first time this ran -
    a check that cries wolf is a check people learn to skip.
    """
    n = re.escape(name)
    patterns = (
        rf'(?<![\w.]){n}\s*\.\s*[A-Z]\w*',       # static member access
        rf'\bnew\s+{n}\b',                        # construction
        rf'\b(?:is|as)\s+{n}\b',                  # pattern / conversion
        rf'[<,]\s*{n}\s*[>,]',                    # generic argument
        rf'\(\s*{n}\s*\)\s*\w',                   # cast
        rf'(?<![\w.]){n}\??\s+\w+\s*[=;,)]',      # declaration or parameter
        rf':\s*{n}\b',                            # base type or constraint
    )
    for pattern in patterns:
        for m in re.finditer(pattern, body):
            yield body[:m.start()].count('\n') + 1


def unresolved_types_check(sources, owned):
    """CS0103/CS0246 before Windows says so.

    MainWindow.xaml.cs referenced OperationState three times with no `using NOBA.Archive.Core.Operations`,
    and the only thing that noticed was the Windows build - a full round trip for a missing line that is
    mechanically obvious from the source. Nothing here compiles the app; it just asks, of every
    project-owned type a file names, whether any namespace the file imports actually contains it.
    """
    problems = 0
    for cs in sorted(sources):
        raw = open(cs, encoding='utf-8').read()
        src = strip_noise(raw)
        visible = visible_namespaces(src)
        known = aliases(src)
        # Names introduced locally would shadow a type of the same name; treat them as resolved.
        known |= set(re.findall(r'\bvar\s+(\w+)\s*=', src))

        body = re.sub(r'^\s*(?:global\s+)?using\s+[^\n]*;', '', src, flags=re.M)
        for name in sorted(owned):
            if name in known or owned[name] & visible:
                continue
            lines = sorted(set(type_positions(body, name)))
            if not lines:
                continue
            where = ' or '.join(sorted(owned[name]))
            print(f"  CS0103  {cs}:{lines[0]}  '{name}' is used as a type but is not in scope"
                  f" ({len(lines)} site(s)); add: using {where};")
            problems += 1
    return problems


# Simple names that Microsoft.UI.Xaml and its neighbours also declare. A type of ours sharing one is
# not a problem until a file has both namespaces open and names it bare — then the compiler refuses to
# choose, and only the Windows build is there to say so.
WINUI_NAMES = {
    'FlowDirection', 'Style', 'Application', 'Window', 'Grid', 'Button', 'Border', 'TextBlock',
    'Thickness', 'Visibility', 'Color', 'Colors', 'Frame', 'Page', 'Point', 'Size', 'Rect', 'Path',
    'Binding', 'Orientation', 'Brush', 'Control', 'Panel', 'ContentDialog', 'ProgressBar', 'CheckBox',
    'RadioButton', 'TextBox', 'ListView', 'ScrollViewer', 'Setter', 'ResourceDictionary', 'DataTemplate',
}


def ambiguous_types_check(sources, owned):
    """CS0104 before Windows says so.

    OperationRow named FlowDirection bare while importing both Microsoft.UI.Xaml and
    NOBA.Archive.Presentation.Bidi, each of which declares one. That is a full round trip through a
    human for a missing namespace prefix — the same price, and the same kind of mistake, as the
    missing using this script was extended for last time.
    """
    problems = 0
    for cs in sorted(sources):
        src = strip_noise(open(cs, encoding='utf-8').read())
        visible = visible_namespaces(src)
        if not any(ns.startswith('Microsoft.UI') for ns in visible):
            continue

        known = aliases(src)
        body = re.sub(r'^\s*(?:global\s+)?using\s+[^\n]*;', '', src, flags=re.M)

        for name in sorted(WINUI_NAMES & owned.keys()):
            if name in known or not owned[name] & visible:
                continue

            # A qualified use is already excluded: every pattern refuses a name preceded by a dot.
            lines = sorted(set(type_positions(body, name)))
            if not lines:
                continue

            ours = ' or '.join(sorted(owned[name]))
            print(f"  CS0104  {cs}:{lines[0]}  '{name}' is ambiguous between {ours} and Microsoft.UI.Xaml"
                  f" ({len(lines)} site(s)); qualify it")
            problems += 1

    return problems



# ---------------------------------------------------------------------------
# CS1061: a member that does not exist on a project type.
#
# The third defect of this shape to cost a Windows round trip. This one was
# `CreateOptionsViewModel.Profiles.IndexOf(...)` - IReadOnlyList<T> has no
# IndexOf, and nothing on the build machine could say so, because the App
# project is the one project that cannot be compiled here.
#
# Narrow on purpose. It only looks at receivers whose declared type is a type
# this repository owns, it resolves members through project base types, and it
# says nothing at all about a type it cannot fully see. A rule that guesses
# would cry wolf, and a check people learn to skip protects nothing.
# ---------------------------------------------------------------------------

def _body_of(src, open_brace):
    """The text between a type's opening brace and its match."""
    depth, i = 0, open_brace
    while i < len(src):
        if src[i] == '{':
            depth += 1
        elif src[i] == '}':
            depth -= 1
            if depth == 0:
                return src[open_brace + 1:i]
        i += 1
    return src[open_brace + 1:]


TYPE_HEADER = re.compile(
    r'\b(?:public|internal)\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+|ref\s+)*'
    r'(class|record|struct|interface)\s+(\w+)([^{;]*)\{')

MEMBER = re.compile(
    r'\b(?:public|internal|protected)\s+'
    r'(?:static\s+|virtual\s+|override\s+|sealed\s+|abstract\s+|async\s+|readonly\s+|new\s+|'
    r'required\s+|partial\s+|extern\s+|unsafe\s+|event\s+|const\s+)*'
    r'([\w<>?\[\],.]+(?:\s*<[^>]*>)?)\s+(\w+)\s*(?:\(|\{|=>|;|=)')

# An interface declares its members with no access modifier at all.
INTERFACE_MEMBER = re.compile(
    r'^\s*(?:new\s+)?(?:event\s+)?([\w<>?\[\],.]+)\s+(\w+)\s*(?:\(|\{|=>|;)', re.M)


def _simple_type(text):
    """The bare type name, or None when it is generic, an array, or otherwise not a plain type."""
    t = text.strip().rstrip('?')
    return t if re.fullmatch(r'[A-Z]\w*', t) else None


def project_members(root='src'):
    """typeName -> (members, bases, memberTypes). Members are everything a caller could name."""
    members, bases, mtypes = {}, {}, {}
    for cs in glob.glob(f'{root}/**/*.cs', recursive=True):
        if '/obj/' in cs or '/bin/' in cs:
            continue
        src = strip_noise(open(cs, encoding='utf-8').read())
        for t in TYPE_HEADER.finditer(src):
            kind, name, header = t.group(1), t.group(2), t.group(3) or ''
            body = _body_of(src, t.end() - 1)
            found = members.setdefault(name, set())

            inherit = header.split(':', 1)[1] if ':' in header else ''
            for b in re.findall(r'\b([A-Z]\w+)', inherit):
                bases.setdefault(name, set()).add(b)

            typed = mtypes.setdefault(name, {})
            for m in MEMBER.finditer(body):
                found.add(m.group(2))
                if _simple_type(m.group(1)):
                    typed.setdefault(m.group(2), _simple_type(m.group(1)))
            for m in re.finditer(r'\b(\w+)\s*=>', body):
                found.add(m.group(1))
            if kind == 'interface':
                for m in INTERFACE_MEMBER.finditer(body):
                    found.add(m.group(2))
                    if _simple_type(m.group(1)):
                        typed.setdefault(m.group(2), _simple_type(m.group(1)))
            if kind == 'record':
                # Positional parameters are properties.
                for m in re.finditer(r'(\w+)\s*[,)]', header.split(':', 1)[0]):
                    found.add(m.group(1))

    return members, bases, mtypes


def type_of_member(type_name, member, members, bases, mtypes, seen=None):
    """The declared type of one member, following project base types."""
    seen = seen or set()
    if type_name in seen:
        return None
    seen.add(type_name)

    if member in mtypes.get(type_name, {}):
        return mtypes[type_name][member]

    for b in bases.get(type_name, ()):
        found = type_of_member(b, member, members, bases, mtypes, seen)
        if found:
            return found

    return None


def members_of(type_name, members, bases, seen=None):
    """Every member visible on a type, or None when a base type is outside this repository."""
    seen = seen or set()
    if type_name in seen:
        return set()
    if type_name not in members:
        return None
    seen.add(type_name)

    out = set(members[type_name])
    for b in bases.get(type_name, ()):
        inherited = members_of(b, members, bases, seen)
        if inherited is not None:
            out |= inherited
        elif not re.match(r'^I[A-Z]', b):
            # An unknown *class* base (Window, UserControl, Exception, ...) brings members this script
            # cannot enumerate, so nothing can be claimed about the type at all. An unknown interface
            # is different: a class must implement what it declares, so those members are already in
            # the class's own body and nothing is missing from the set.
            return None

    return out


# Types whose full member set this script cannot see, so nothing is claimed about them.
OPAQUE = re.compile(r'^(?:I?ReadOnly|List|Dictionary|HashSet|Task|ValueTask|Nullable|IEnumerable|IList|ICollection)')


def missing_members_check(sources, owned):
    """Report `receiver.Member` where the receiver's declared type is ours and has no such member."""
    members, bases, mtypes = project_members()
    problems = 0

    for cs in sorted(sources):
        src = strip_noise(open(cs, encoding='utf-8').read())

        # Receivers declared in this file: fields, properties and expression-bodied properties whose
        # declared type is a project type with no generic arguments.
        receivers = {}
        for m in re.finditer(
                r'\b(?:private|public|internal|protected)\s+(?:readonly\s+|static\s+)*'
                r'([A-Z]\w+)\??\s+(\w+)\s*(?:;|=|\{|=>)', src):
            declared, name = m.group(1), m.group(2)
            if declared in members and not OPAQUE.match(declared):
                receivers[name] = declared

        for name, declared in sorted(receivers.items()):
            # The whole chain, not just the first hop: the defect this rule was written for was
            # `Model.Options.IndexOf(...)`, two levels down.
            for m in re.finditer(rf'(?<![\w.]){re.escape(name)}((?:\s*!?\??\.\s*\w+)+)', src):
                current = declared
                walked = name

                for member in re.findall(r'\.\s*(\w+)', m.group(1)):
                    known = members_of(current, members, bases)
                    if not known:
                        # None: a base outside this repository. Empty: nothing declared. Either way
                        # this type cannot be judged, and neither can anything past it.
                        break

                    if member not in known:
                        line = src[:m.start()].count('\n') + 1
                        print(f'  CS1061  {os.path.basename(cs)}:{line} '
                              f'{current} has no member named "{member}" ({walked}.{member})')
                        problems += 1
                        break

                    walked = f'{walked}.{member}'
                    current = type_of_member(current, member, members, bases, mtypes)
                    if not current:
                        break

    return problems


def hosted_pages_check():
    """Every page hosted in the shell must have its lifecycle methods actually called.

    A UserControl that declares Bind/ApplyLanguage/Render and never has them called compiles
    perfectly and renders blank - which is exactly how the operations screen shipped with no title,
    no empty-state text and a wordless button. Nothing on Linux can catch that by compiling; this
    can, by reading the shell for the calls.
    """
    window = f'{APP}/MainWindow.xaml'
    behind = f'{APP}/MainWindow.xaml.cs'
    if not (os.path.isfile(window) and os.path.isfile(behind)):
        return 0

    markup = open(window, encoding='utf-8').read()
    code = open(behind, encoding='utf-8').read()
    problems = 0

    for m in re.finditer(r'<views:(\w+)\s+x:Name="(\w+)"', markup):
        page_type, name = m.group(1), m.group(2)
        page_cs = f'{APP}/Views/{page_type}.xaml.cs'
        if not os.path.isfile(page_cs):
            continue
        page_src = open(page_cs, encoding='utf-8').read()
        for hook in ('Bind', 'ApplyLanguage'):
            if re.search(rf'public\s+void\s+{hook}\s*\(', page_src) \
                    and not re.search(rf'\b{name}\.{hook}\s*\(', code):
                print(f"  ORPHAN  {page_type} declares {hook}() and the shell never calls {name}.{hook}()")
                problems += 1

    return problems


def main():
    if not os.path.isdir(APP):
        print(f"run from the repository root; '{APP}' not found")
        return 1

    xaml_files = glob.glob(f'{APP}/**/*.xaml', recursive=True)
    all_names = set()
    per_file = {}
    for x in xaml_files:
        names = set(re.findall(r'x:Name="(\w+)"', open(x, encoding='utf-8').read()))
        all_names |= names
        per_file[os.path.splitext(x)[0]] = names

    problems = 0

    clash = all_names & SHADOWABLE
    for c in sorted(clash):
        print(f'  SHADOW  x:Name="{c}" shadows a type the code-behind uses')
    problems += len(clash)
    problems += hosted_pages_check()

    # The whole project, not only the code-behind: the analyzer does not stop at Views/.
    sources = [f for f in glob.glob(f'{APP}/**/*.cs', recursive=True)
               if '/obj/' not in f and '/bin/' not in f and not f.endswith('.g.cs')]

    for cs in sorted(sources):
        stem = cs[:-3] if cs.endswith('.cs') else cs
        names = per_file.get(stem[:-5] if stem.endswith('.xaml') else stem, set())
        type_name = os.path.basename(stem).replace('.xaml', '')
        problems += check_file(cs, names, type_name)

    owned = declared_types()
    problems += unresolved_types_check(sources, owned)
    problems += ambiguous_types_check(sources, owned)
    problems += missing_members_check(sources, owned)

    print(f"scanned {len(sources)} source files and {len(xaml_files)} markup files "
          f"against {len(owned)} project types")
    print("ANALYZER PRE-FLIGHT:",
          "FAIL" if problems else "PASS - no CS0103/CS0104/CS1061/CA1822/CA1859/shadowing risk in the app project")
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
