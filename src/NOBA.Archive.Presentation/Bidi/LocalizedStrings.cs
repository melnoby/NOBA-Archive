namespace NOBA.Archive.Presentation.Bidi;

/// <summary>
/// The UI's text, in both languages, as data rather than as resource files.
/// </summary>
/// <remarks>
/// <para>
/// A <c>.resw</c> pair would be the conventional choice and would put every string beyond the reach of
/// a test that runs where WinUI cannot be built. Here the strings are a dictionary per language, which
/// means <c>L10N-001</c> can assert that the two define exactly the same keys — the single most common
/// localisation defect, and one that shows up as an empty label rather than as an error.
/// </para>
/// <para>
/// Arabic is written first in each pair on purpose. It is not the translation of the English; both are
/// the product's text, and the ordering is a small reminder of that.
/// </para>
/// </remarks>
public sealed class LocalizedStrings
{
    private readonly IReadOnlyDictionary<string, string> _values;

    private LocalizedStrings(UiLanguage language, IReadOnlyDictionary<string, string> values)
    {
        Language = language;
        _values = values;
    }

    public UiLanguage Language { get; }

    public static LocalizedStrings For(UiLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return new LocalizedStrings(language, language == UiLanguage.Arabic ? ArabicValues : EnglishValues);
    }

    /// <summary>Every key the UI uses. Missing keys fail <c>L10N-001</c> rather than rendering blank.</summary>
    public static IReadOnlyCollection<string> Keys => EnglishValues.Keys;

    public string this[string key] => _values.TryGetValue(key, out var value) ? value : key;

    // Shell
    public string AppName => this["app.name"];

    public string NavBrowse => this["nav.browse"];

    public string NavCreate => this["nav.create"];

    public string NavOperations => this["nav.operations"];

    public string NavSettings => this["nav.settings"];

    public string ToggleTheme => this["action.toggleTheme"];

    public string ToggleLanguage => this["action.toggleLanguage"];

    // Browse
    public string OpenArchive => this["browse.open"];

    public string ExtractAll => this["browse.extractAll"];

    public string ExtractSelected => this["browse.extractSelected"];

    public string TestArchive => this["browse.test"];

    public string SearchPlaceholder => this["browse.searchPlaceholder"];

    public string EmptyTitle => this["browse.emptyTitle"];

    public string EmptyBody => this["browse.emptyBody"];

    public string ColumnName => this["column.name"];

    public string ColumnSize => this["column.size"];

    public string ColumnPacked => this["column.packed"];

    public string ColumnModified => this["column.modified"];

    public string BlockedLabel => this["state.blocked"];

    public string EncryptedLabel => this["state.encrypted"];

    public string GuessedEncodingWarning => this["warning.guessedEncoding"];

    // Create
    public string CreateAddFiles => this["create.addFiles"];

    public string CreateAddFolder => this["create.addFolder"];

    public string CreateRemove => this["create.remove"];

    public string CreateClear => this["create.clear"];

    public string CreateInputs => this["create.inputs"];

    public string CreateNoInputs => this["create.noInputs"];

    public string CreateDestination => this["create.destination"];

    public string CreateChooseDestination => this["create.chooseDestination"];

    public string CreateName => this["create.name"];

    public string CreateStart => this["create.start"];

    public string CreateTargetLabel => this["create.target"];

    public string CreateNeedsInputs => this["create.needsInputs"];

    public string CreateNeedsDestination => this["create.needsDestination"];

    public string CreateNeedsName => this["create.needsName"];

    public string CreateNoFormat => this["create.noFormat"];

    public string CreateDone => this["create.done"];

    public string CreateCancelled => this["create.cancelled"];

    public string CreateFailed => this["create.failed"];

    public string CreateExists => this["create.exists"];

    public string CreateOverwrite => this["create.overwrite"];

    public string CreateTitle => this["create.title"];

    public string CreateFormat => this["create.format"];

    public string CreateLevel => this["create.level"];

    /// <summary>The four compression profiles, named for what a person is choosing between.</summary>
    public string ProfileFast => this["create.profile.fast"];

    public string ProfileBalanced => this["create.profile.balanced"];

    public string ProfileMaximum => this["create.profile.maximum"];

    public string ProfileSmallest => this["create.profile.smallest"];

    public string CreateSolid => this["create.solid"];

    public string CreateEncrypt => this["create.encrypt"];

    public string CreateEncryptNames => this["create.encryptNames"];

    public string CreatePassword => this["create.password"];

    public string UnsupportedByFormat => this["create.unsupportedByFormat"];

    public string RequiresEncryption => this["create.requiresEncryption"];

    // Drag and drop
    public string DropIdle => this["drop.idle"];

    public string DropActive => this["drop.active"];

    public string DropInvalid => this["drop.invalid"];

    public string DropSelfTarget => this["drop.selfTarget"];

    public string DropUnreachable => this["drop.unreachable"];

    public string DropNotAFile => this["drop.notAFile"];

    public string DropAlreadyListed => this["drop.alreadyListed"];

    public string CreateSingleMemberFormat => this["create.singleMemberFormat"];

    // Shell integration
    public string ShellMenu => this["shell.menu"];

    public string ShellOpen => this["shell.open"];

    public string ShellTest => this["shell.test"];

    public string ShellExtractHere => this["shell.extractHere"];

    public string ShellExtractTo => this["shell.extractTo"];

    public string ShellCompressTo => this["shell.compressTo"];

    public string ShellCreateArchive => this["shell.createArchive"];

    public string ShellSingleMemberFormat => this["shell.singleMemberFormat"];

    // Password prompt
    public string PasswordTitle => this["password.title"];

    public string PasswordBody => this["password.body"];

    public string PasswordEntryBody => this["password.entryBody"];

    public string PasswordField => this["password.field"];

    public string PasswordReveal => this["password.reveal"];

    public string PasswordSubmit => this["password.submit"];

    public string PasswordCancel => this["password.cancel"];

    public string PasswordRetry => this["password.retry"];

    // Conflict prompt
    public string ConflictTitle => this["conflict.title"];

    public string ConflictBody => this["conflict.body"];

    public string ConflictPath => this["conflict.path"];

    public string ConflictExisting => this["conflict.existing"];

    public string ConflictIncoming => this["conflict.incoming"];

    public string ConflictOverwrite => this["conflict.overwrite"];

    public string ConflictSkip => this["conflict.skip"];

    public string ConflictRename => this["conflict.rename"];

    public string ConflictCancel => this["conflict.cancel"];

    public string ConflictApplyToAll => this["conflict.applyToAll"];

    public string ConflictContinue => this["conflict.continue"];

    // File list
    public string ColumnType => this["column.type"];

    public string ColumnAttributes => this["column.attributes"];

    public string TypeFolder => this["type.folder"];

    public string TypeFile => this["type.file"];

    public string ParentFolder => this["browse.parentFolder"];

    public string ClearSearch => this["browse.clearSearch"];

    public string ItemsLabel => this["browse.items"];

    public string SelectedLabel => this["browse.selected"];

    public string TotalSizeLabel => this["browse.totalSize"];

    public string DropHint => this["browse.dropHint"];

    public string NoMatches => this["browse.noMatches"];

    public string FilesLabel => this["browse.files"];

    public string FoldersLabel => this["browse.folders"];

    public string GoUpLabel => this["browse.up"];

    public string GoHomeLabel => this["browse.home"];

    public string SolidLabel => this["browse.solid"];

    public string VolumesLabel => this["browse.volumes"];

    // Archive test. The engine distinguishes a damaged archive from one this build simply has no
    // decoder for, and the UI has to keep that distinction: telling someone to restore a backup for a
    // file that is perfectly intact is the worse of the two errors.
    public string TestRunning => this["test.running"];

    public string TestValid => this["test.valid"];

    public string TestWarnings => this["test.warnings"];

    public string TestCorrupted => this["test.corrupted"];

    public string TestUnsupported => this["test.unsupported"];

    public string TestPasswordRequired => this["test.passwordRequired"];

    public string TestFailed => this["test.failed"];

    public string TestCleanDetail => this["test.cleanDetail"];

    public string TestUnsupportedDetail => this["test.unsupportedDetail"];

    public string TestEntriesLabel => this["test.entries"];

    public string TestFindingsLabel => this["test.findings"];

    public string TestErrorChip => this["test.errorChip"];

    public string TestWarningChip => this["test.warningChip"];

    public string TestDismiss => this["test.dismiss"];

    // Operations. The queue owns these states; the UI only names them.
    public string OperationQueued => this["op.queued"];

    public string OperationRunning => this["op.running"];

    public string OperationPaused => this["op.paused"];

    public string OperationCompleted => this["op.completed"];

    public string OperationCancelled => this["op.cancelled"];

    /// <summary>Cancellation asked for, work not stopped yet. Deliberately distinct from "Cancelled".</summary>
    public string OperationCancelling => this["op.cancelling"];

    // ---- formats: why something is or is not on offer ----

    /// <summary>Composed with the format's own name, e.g. "7Z". Takes one argument.</summary>
    public string FormatProviderUnavailable => this["format.unavailable"];

    public string FormatCannotCreate => this["format.cannotCreate"];

    public string FormatCannotTest => this["format.cannotTest"];

    public string FormatCannotExtract => this["format.cannotExtract"];

    public string FormatCannotUpdate => this["format.cannotUpdate"];

    public string FormatCannotOpen => this["format.cannotOpen"];

    /// <summary>Shown after a clean test on a format that checksums nothing but its own structure.</summary>
    public string FormatNoContentChecksum => this["format.noContentChecksum"];

    // ---- the extract dialog ----

    public string ExtractTitle => this["extract.title"];

    public string ExtractBody => this["extract.body"];

    public string ExtractArchiveLabel => this["extract.archive"];

    public string ExtractScopeLabel => this["extract.scope"];

    public string ExtractScopeAll => this["extract.scope.all"];

    public string ExtractScopeSelection => this["extract.scope.selection"];

    public string ExtractDestinationLabel => this["extract.destination"];

    public string ExtractChooseDestination => this["extract.choose"];

    public string ExtractNoDestination => this["extract.noDestination"];

    public string ExtractConflictLabel => this["extract.conflict"];

    public string ExtractConflictAsk => this["extract.conflict.ask"];

    public string ExtractConflictOverwrite => this["extract.conflict.overwrite"];

    public string ExtractConflictSkip => this["extract.conflict.skip"];

    public string ExtractConflictRename => this["extract.conflict.rename"];

    public string ExtractConflictFail => this["extract.conflict.fail"];

    public string ExtractConflictHint => this["extract.conflict.hint"];

    public string ExtractStart => this["extract.start"];

    public string OperationFailed => this["op.failed"];

    public string OperationPause => this["op.pause"];

    public string OperationResume => this["op.resume"];

    public string OperationCancel => this["op.cancel"];

    public string OperationEtaLabel => this["op.eta"];

    /// <summary>Shown instead of a percentage the engine could not compute — never instead of zero.</summary>
    public string OperationIndeterminate => this["op.indeterminate"];

    public string OperationExtracting => this["op.extracting"];

    public string OperationTookLabel => this["op.took"];

    /// <summary>Precedes the destination folder: "to C:\...".</summary>
    public string OperationToLabel => this["op.to"];

    public string OperationTesting => this["op.testing"];

    public string OperationCreating => this["op.creating"];

    public string OperationClearFinished => this["op.clearFinished"];

    public string OperationFailedDetail => this["op.failedDetail"];

    public string OperationsEmptyTitle => this["op.emptyTitle"];

    public string OperationsEmptyBody => this["op.emptyBody"];

    /// <summary>Why a nav item is dark: the feature is not built, and the UI says so rather than lying.</summary>
    public string NotBuiltYet => this["nav.notBuilt"];


    private static readonly Dictionary<string, string> ArabicValues = new(StringComparer.Ordinal)
    {
        ["app.name"] = "نوبا للأرشفة",
        ["nav.browse"] = "تصفّح",
        ["nav.create"] = "إنشاء",
        ["nav.operations"] = "العمليات",
        ["nav.settings"] = "الإعدادات",
        ["action.toggleTheme"] = "تبديل السمة",
        ["action.toggleLanguage"] = "English",
        ["browse.open"] = "فتح أرشيف",
        ["browse.extractAll"] = "استخراج الكل",
        ["browse.extractSelected"] = "استخراج المحدد",
        ["browse.test"] = "فحص",
        ["browse.searchPlaceholder"] = "ابحث في الأرشيف",
        ["browse.emptyTitle"] = "لا يوجد أرشيف مفتوح",
        ["browse.emptyBody"] = "افتح أرشيفًا بصيغة \u2066ZIP / 7Z / RAR\u2069، أو أفلته هنا.",
        ["column.name"] = "الاسم",
        ["column.size"] = "الحجم",
        ["column.packed"] = "المضغوط",
        ["column.modified"] = "التعديل",
        ["state.blocked"] = "مرفوض",
        ["state.encrypted"] = "مشفَّر",
        ["warning.guessedEncoding"] = "ترميز الأسماء مُخمَّن — قد تظهر الأسماء خطأ.",
        ["create.addFiles"] = "إضافة ملفات",
        ["create.addFolder"] = "إضافة مجلد",
        ["create.remove"] = "إزالة",
        ["create.clear"] = "تفريغ",
        ["create.inputs"] = "المحتويات",
        ["create.noInputs"] = "لم تُضَف ملفات أو مجلدات بعد",
        ["create.destination"] = "المجلد الوجهة",
        ["create.chooseDestination"] = "اختيار المجلد",
        ["create.name"] = "اسم الأرشيف",
        ["create.start"] = "إنشاء الأرشيف",
        ["create.target"] = "سيُكتَب إلى",
        ["create.needsInputs"] = "أضف ملفًا أو مجلدًا واحدًا على الأقل",
        ["create.needsDestination"] = "اختر المجلد الذي سيُحفَظ فيه الأرشيف",
        ["create.needsName"] = "اكتب اسمًا للأرشيف",
        ["create.noFormat"] = "لا توجد صيغة يستطيع هذا الإصدار كتابتها",
        ["create.done"] = "تم إنشاء الأرشيف",
        ["create.cancelled"] = "أُلغِي الإنشاء — لم يُكتَب أرشيف",
        ["create.failed"] = "فشل الإنشاء",
        ["create.exists"] = "يوجد ملف بهذا الاسم بالفعل",
        ["create.overwrite"] = "استبدال الملف الموجود",
        ["create.title"] = "إنشاء أرشيف",
        ["create.format"] = "الصيغة",
        ["create.level"] = "مستوى الضغط",
        ["create.profile.fast"] = "سريع",
        ["create.profile.balanced"] = "متوازن",
        ["create.profile.maximum"] = "أقصى ضغط",
        ["create.profile.smallest"] = "أصغر حجم",
        ["create.solid"] = "ضغط صلب",
        ["create.encrypt"] = "تشفير المحتوى",
        ["create.encryptNames"] = "تشفير أسماء الملفات",
        ["create.password"] = "كلمة المرور",
        ["create.unsupportedByFormat"] = "غير مدعوم في هذه الصيغة",
        ["create.requiresEncryption"] = "يتطلب تفعيل التشفير أولًا",
        ["drop.idle"] = "اسحب الملفات أو المجلدات هنا",
        ["drop.active"] = "أفلت لإضافة العناصر",
        ["drop.invalid"] = "لا يمكن إضافة هذا المحتوى",
        ["drop.selfTarget"] = "لا يمكن استخدام الأرشيف الناتج كمصدر لنفسه.",
        ["drop.unreachable"] = "تعذّر الوصول إلى بعض العناصر، فلم تُضَف.",
        ["drop.notAFile"] = "بعض العناصر ليست ملفات أو مجلدات، فلم تُضَف.",
        ["drop.alreadyListed"] = "العناصر مضافة بالفعل.",
        ["create.singleMemberFormat"] = "تحتوي هذه الصيغة على ملف واحد فقط. أزل العناصر الزائدة أو اختر صيغة أخرى.",
        ["shell.menu"] = "نوبا أرشيف",
        ["shell.open"] = "فتح باستخدام نوبا أرشيف",
        ["shell.test"] = "فحص سلامة الأرشيف",
        ["shell.extractHere"] = "استخراج هنا",
        ["shell.extractTo"] = "استخراج إلى…",
        ["shell.compressTo"] = "ضغط إلى {0}",
        ["shell.createArchive"] = "إنشاء أرشيف…",
        ["shell.singleMemberFormat"] = "تحتوي هذه الصيغة على ملف واحد فقط",
        ["password.title"] = "أرشيف مشفَّر",
        ["password.body"] = "هذا الأرشيف يحتاج كلمة مرور قبل قراءة محتواه.",
        ["password.entryBody"] = "هذا المدخل مشفَّر ويحتاج كلمة مرور.",
        ["password.field"] = "كلمة المرور",
        ["password.reveal"] = "إظهار كلمة المرور",
        ["password.submit"] = "فتح",
        ["password.cancel"] = "إلغاء",
        ["password.retry"] = "كلمة المرور السابقة لم تكن صحيحة.",
        ["conflict.title"] = "يوجد ملف بهذا الاسم",
        ["conflict.body"] = "في وجهة الاستخراج ملف يحمل الاسم نفسه. اختر ما يحدث له.",
        ["conflict.path"] = "المسار",
        ["conflict.existing"] = "الموجود على القرص",
        ["conflict.incoming"] = "القادم من الأرشيف",
        ["conflict.overwrite"] = "استبدال الموجود",
        ["conflict.skip"] = "تخطّي هذا الملف",
        ["conflict.rename"] = "الاحتفاظ بالاثنين",
        ["conflict.cancel"] = "إلغاء العملية",
        ["conflict.applyToAll"] = "طبّق هذا الاختيار على كل تعارض تالٍ",
        ["conflict.continue"] = "متابعة",
        ["column.type"] = "النوع",
        ["column.attributes"] = "الخصائص",
        ["type.folder"] = "مجلد",
        ["type.file"] = "ملف",
        ["browse.parentFolder"] = "المجلد الأصل",
        ["browse.clearSearch"] = "مسح البحث",
        ["browse.items"] = "العناصر",
        ["browse.selected"] = "المحدد",
        ["browse.totalSize"] = "إجمالي الحجم",
        ["browse.dropHint"] = "أو أفلت أرشيفًا في هذه النافذة",
        ["browse.noMatches"] = "لا نتائج مطابقة",
        ["browse.files"] = "ملفات",
        ["browse.folders"] = "مجلدات",
        ["browse.up"] = "للأعلى",
        ["browse.home"] = "جذر الأرشيف",
        ["browse.solid"] = "أرشيف متماسك",
        ["browse.volumes"] = "أجزاء",
        ["test.running"] = "جارٍ فحص الأرشيف…",
        ["test.valid"] = "الأرشيف سليم",
        ["test.warnings"] = "الأرشيف قابل للاستخراج، مع ملاحظات",
        ["test.corrupted"] = "الأرشيف تالف",
        ["test.unsupported"] = "طريقة ضغط لا تدعمها هذه النسخة",
        ["test.passwordRequired"] = "الفحص يحتاج كلمة المرور",
        ["test.failed"] = "تعذّر إكمال الفحص",
        ["test.cleanDetail"] = "لا خطأ في مجموع التحقّق",
        ["test.unsupportedDetail"] = "الملف على الأرجح سليم — الناقص برنامج فكّ الضغط، لا البيانات",
        ["test.entries"] = "مدخلًا مفحوصًا",
        ["test.findings"] = "ملاحظة",
        ["test.errorChip"] = "تالف",
        ["test.warningChip"] = "تحذير",
        ["test.dismiss"] = "إغلاق النتيجة",
        ["op.queued"] = "في الانتظار",
        ["op.running"] = "جارٍ التنفيذ",
        ["op.paused"] = "موقوفة مؤقتًا",
        ["op.completed"] = "اكتملت",
        ["op.cancelled"] = "أُلغيت",
        ["op.cancelling"] = "جارٍ الإلغاء",
        ["format.unavailable"] = "تنسيق {0} غير متاح في هذا التثبيت لأن مكوّن المحرك المطلوب غير موجود.",
        ["format.cannotCreate"] = "لا يمكن إنشاء أرشيف بتنسيق {0}.",
        ["format.cannotTest"] = "لا يمكن فحص سلامة أرشيف بتنسيق {0}.",
        ["format.cannotExtract"] = "لا يمكن استخراج أرشيف بتنسيق {0}.",
        ["format.cannotUpdate"] = "لا يمكن تعديل أرشيف {0} بعد إنشائه.",
        ["format.cannotOpen"] = "لا يمكن فتح أرشيف بتنسيق {0}.",
        ["format.noContentChecksum"] = "هذا التنسيق لا يحفظ مجموع تحقق للمحتوى، لذلك جرى التحقق من بنية الأرشيف وأطوال عناصره فقط.",
        ["extract.title"] = "استخراج",
        ["extract.body"] = "اختر المكان الذي ستوضع فيه الملفات، وما يحدث إذا كان الملف موجودًا هناك بالفعل.",
        ["extract.archive"] = "الأرشيف",
        ["extract.scope"] = "المحتوى",
        ["extract.scope.all"] = "كل الملفات",
        ["extract.scope.selection"] = "الملفات المحددة",
        ["extract.destination"] = "الوجهة",
        ["extract.choose"] = "اختيار مجلد",
        ["extract.noDestination"] = "لم يُختَر مجلد بعد",
        ["extract.conflict"] = "عند وجود ملف بنفس الاسم",
        ["extract.conflict.ask"] = "اسألني في كل مرة",
        ["extract.conflict.overwrite"] = "استبدال الملف الموجود",
        ["extract.conflict.skip"] = "تخطّي الملف والإبقاء على الموجود",
        ["extract.conflict.rename"] = "الاحتفاظ بالاثنين باسم جديد",
        ["extract.conflict.fail"] = "إيقاف الاستخراج",
        ["extract.conflict.hint"] = "لا يُستبدل أي ملف موجود دون قرار صريح.",
        ["extract.start"] = "استخراج",
        ["op.failed"] = "أخفقت",
        ["op.pause"] = "إيقاف مؤقت",
        ["op.resume"] = "استئناف",
        ["op.cancel"] = "إلغاء",
        ["op.eta"] = "المتبقّي",
        ["op.indeterminate"] = "التقدّم غير محسوب",
        ["op.extracting"] = "استخراج",
        ["op.took"] = "في",
        ["op.to"] = "إلى",
        ["op.testing"] = "اختبار",
        ["op.creating"] = "إنشاء",
        ["op.clearFinished"] = "مسح المنتهية",
        ["op.failedDetail"] = "سبب الفشل",
        ["op.emptyTitle"] = "لا توجد عمليات",
        ["op.emptyBody"] = "ما تبدأه من استخراج أو فحص يظهر هنا، ويمكنك إيقافه أو إلغاؤه.",
        ["nav.notBuilt"] = "لم تُبنَ بعد",
    };

    private static readonly Dictionary<string, string> EnglishValues = new(StringComparer.Ordinal)
    {
        ["app.name"] = "NOBA Archive",
        ["nav.browse"] = "Browse",
        ["nav.create"] = "Create",
        ["nav.operations"] = "Operations",
        ["nav.settings"] = "Settings",
        ["action.toggleTheme"] = "Switch theme",
        ["action.toggleLanguage"] = "العربية",
        ["browse.open"] = "Open archive",
        ["browse.extractAll"] = "Extract all",
        ["browse.extractSelected"] = "Extract selected",
        ["browse.test"] = "Test",
        ["browse.searchPlaceholder"] = "Search this archive",
        ["browse.emptyTitle"] = "No archive open",
        ["browse.emptyBody"] = "Open a ZIP, 7Z or RAR archive, or drop one here.",
        ["column.name"] = "Name",
        ["column.size"] = "Size",
        ["column.packed"] = "Packed",
        ["column.modified"] = "Modified",
        ["state.blocked"] = "Blocked",
        ["state.encrypted"] = "Encrypted",
        ["warning.guessedEncoding"] = "Entry names were decoded by guesswork and may be wrong.",
                ["create.addFiles"] = "Add files",
        ["create.addFolder"] = "Add folder",
        ["create.remove"] = "Remove",
        ["create.clear"] = "Clear",
        ["create.inputs"] = "Contents",
        ["create.noInputs"] = "No files or folders added yet",
        ["create.destination"] = "Destination folder",
        ["create.chooseDestination"] = "Choose folder",
        ["create.name"] = "Archive name",
        ["create.start"] = "Create archive",
        ["create.target"] = "Will be written to",
        ["create.needsInputs"] = "Add at least one file or folder",
        ["create.needsDestination"] = "Choose where the archive should go",
        ["create.needsName"] = "Give the archive a name",
        ["create.noFormat"] = "This build cannot write any format",
        ["create.done"] = "Archive created",
        ["create.cancelled"] = "Creation cancelled - no archive was written",
        ["create.failed"] = "Creation failed",
        ["create.exists"] = "A file of that name already exists",
        ["create.overwrite"] = "Replace the existing file",
["create.title"] = "Create archive",
        ["create.format"] = "Format",
        ["create.level"] = "Compression level",
        ["create.profile.fast"] = "Fast",
        ["create.profile.balanced"] = "Balanced",
        ["create.profile.maximum"] = "Maximum compression",
        ["create.profile.smallest"] = "Smallest size",
        ["create.solid"] = "Solid compression",
        ["create.encrypt"] = "Encrypt contents",
        ["create.encryptNames"] = "Encrypt file names",
        ["create.password"] = "Password",
        ["create.unsupportedByFormat"] = "Not supported by this format",
        ["create.requiresEncryption"] = "Turn on encryption first",
        ["drop.idle"] = "Drag files or folders here",
        ["drop.active"] = "Release to add",
        ["drop.invalid"] = "This content cannot be added",
        ["drop.selfTarget"] = "The destination archive cannot be used as its own input.",
        ["drop.unreachable"] = "Some items could not be reached and were not added.",
        ["drop.notAFile"] = "Some items are not files or folders and were not added.",
        ["drop.alreadyListed"] = "Already in the list.",
        ["create.singleMemberFormat"] = "This format holds a single file. Remove the extra items, or choose another format.",
        ["shell.menu"] = "NOBA Archive",
        ["shell.open"] = "Open with NOBA Archive",
        ["shell.test"] = "Test archive",
        ["shell.extractHere"] = "Extract here",
        ["shell.extractTo"] = "Extract to…",
        ["shell.compressTo"] = "Compress to {0}",
        ["shell.createArchive"] = "Create archive…",
        ["shell.singleMemberFormat"] = "This format holds a single file",
        ["password.title"] = "Encrypted archive",
        ["password.body"] = "This archive needs a password before its contents can be read.",
        ["password.entryBody"] = "This entry is encrypted and needs a password.",
        ["password.field"] = "Password",
        ["password.reveal"] = "Show password",
        ["password.submit"] = "Unlock",
        ["password.cancel"] = "Cancel",
        ["password.retry"] = "The previous password was not correct.",
        ["conflict.title"] = "A file with this name exists",
        ["conflict.body"] = "The destination already holds a file with this name. Choose what happens to it.",
        ["conflict.path"] = "Path",
        ["conflict.existing"] = "Already on disk",
        ["conflict.incoming"] = "From the archive",
        ["conflict.overwrite"] = "Replace what is there",
        ["conflict.skip"] = "Skip this file",
        ["conflict.rename"] = "Keep both",
        ["conflict.cancel"] = "Cancel the operation",
        ["conflict.applyToAll"] = "Apply this to every later conflict",
        ["conflict.continue"] = "Continue",
        ["column.type"] = "Type",
        ["column.attributes"] = "Attributes",
        ["type.folder"] = "Folder",
        ["type.file"] = "File",
        ["browse.parentFolder"] = "Parent folder",
        ["browse.clearSearch"] = "Clear search",
        ["browse.items"] = "Items",
        ["browse.selected"] = "Selected",
        ["browse.totalSize"] = "Total size",
        ["browse.dropHint"] = "or drop an archive into this window",
        ["browse.noMatches"] = "Nothing matches",
        ["browse.files"] = "files",
        ["browse.folders"] = "folders",
        ["browse.up"] = "Up",
        ["browse.home"] = "Archive root",
        ["browse.solid"] = "Solid archive",
        ["browse.volumes"] = "volumes",
        ["test.running"] = "Testing the archive…",
        ["test.valid"] = "The archive is intact",
        ["test.warnings"] = "Extractable, with notes",
        ["test.corrupted"] = "The archive is damaged",
        ["test.unsupported"] = "A compression method this build cannot decode",
        ["test.passwordRequired"] = "Testing needs the password",
        ["test.failed"] = "The test could not finish",
        ["test.cleanDetail"] = "no checksum errors",
        ["test.unsupportedDetail"] = "The file is most likely intact — what is missing is a decoder, not the data",
        ["test.entries"] = "entries tested",
        ["test.findings"] = "notes",
        ["test.errorChip"] = "Damaged",
        ["test.warningChip"] = "Warning",
        ["test.dismiss"] = "Dismiss result",
        ["op.queued"] = "Waiting",
        ["op.running"] = "Running",
        ["op.paused"] = "Paused",
        ["op.completed"] = "Finished",
        ["op.cancelled"] = "Cancelled",
        ["op.cancelling"] = "Cancelling",
        ["format.unavailable"] = "{0} is unavailable in this installation because the engine component it needs is missing.",
        ["format.cannotCreate"] = "{0} archives cannot be created.",
        ["format.cannotTest"] = "{0} archives cannot be integrity-tested.",
        ["format.cannotExtract"] = "{0} archives cannot be extracted.",
        ["format.cannotUpdate"] = "{0} archives cannot be changed once written.",
        ["format.cannotOpen"] = "{0} archives cannot be opened.",
        ["format.noContentChecksum"] = "This format stores no checksum over file contents, so the archive's structure and entry lengths were verified, not the data itself.",
        ["extract.title"] = "Extract",
        ["extract.body"] = "Choose where the files go, and what happens if one is already there.",
        ["extract.archive"] = "Archive",
        ["extract.scope"] = "Contents",
        ["extract.scope.all"] = "All files",
        ["extract.scope.selection"] = "Selected files",
        ["extract.destination"] = "Destination",
        ["extract.choose"] = "Choose folder",
        ["extract.noDestination"] = "No folder chosen yet",
        ["extract.conflict"] = "If a file is already there",
        ["extract.conflict.ask"] = "Ask me each time",
        ["extract.conflict.overwrite"] = "Replace the existing file",
        ["extract.conflict.skip"] = "Keep the existing file",
        ["extract.conflict.rename"] = "Keep both, under a new name",
        ["extract.conflict.fail"] = "Stop the extraction",
        ["extract.conflict.hint"] = "Nothing already on disk is replaced without an explicit decision.",
        ["extract.start"] = "Extract",
        ["op.failed"] = "Failed",
        ["op.pause"] = "Pause",
        ["op.resume"] = "Resume",
        ["op.cancel"] = "Cancel",
        ["op.eta"] = "left",
        ["op.indeterminate"] = "progress cannot be measured",
        ["op.extracting"] = "Extracting",
        ["op.took"] = "in",
        ["op.to"] = "to",
        ["op.testing"] = "Testing",
        ["op.creating"] = "Creating",
        ["op.clearFinished"] = "Clear finished",
        ["op.failedDetail"] = "Reason",
        ["op.emptyTitle"] = "Nothing running",
        ["op.emptyBody"] = "Extractions and tests you start appear here, where you can pause or cancel them.",
        ["nav.notBuilt"] = "Not built yet",
    };
}
