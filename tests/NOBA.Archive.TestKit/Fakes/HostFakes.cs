using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.TestKit.Fakes;

/// <summary>A conflict prompt that answers from a scripted queue, so "...All" latching is testable.</summary>
public sealed class ScriptedConflictPrompt : IConflictPrompt
{
    private readonly Queue<ConflictDecision> _answers;

    public ScriptedConflictPrompt(params ConflictDecision[] answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        _answers = new Queue<ConflictDecision>(answers);
    }

    public int CallCount { get; private set; }

    public ValueTask<ConflictDecision> ResolveAsync(ConflictRequest request, CancellationToken cancellationToken)
    {
        CallCount++;
        if (_answers.Count == 0)
        {
            throw new InvalidOperationException("The conflict prompt was called more times than the test scripted.");
        }

        return ValueTask.FromResult(_answers.Dequeue());
    }
}

/// <summary>Hands out a fixed password. The test owns the lifetime.</summary>
public sealed class ScriptedPasswordProvider : IPasswordProvider, IDisposable
{
    private readonly Password? _password;

    public ScriptedPasswordProvider(string? password) =>
        _password = password is null ? null : Password.FromChars(password);

    public int CallCount { get; private set; }

    public ValueTask<Password?> GetPasswordAsync(PasswordRequest request, CancellationToken cancellationToken)
    {
        CallCount++;
        return ValueTask.FromResult(_password);
    }

    public void Dispose() => _password?.Dispose();
}
