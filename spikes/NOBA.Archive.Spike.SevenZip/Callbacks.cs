using System.Runtime.InteropServices;

namespace NOBA.Archive.Spike.SevenZip;

/// <summary>
/// <c>ICryptoGetTextPassword</c> as its own object with its own vtable, handed out by
/// <see cref="ComObjectBase.QueryOtherInterface"/>.
/// </summary>
/// <remarks>
/// The password crosses as a BSTR the handler frees. It never touches a command line, an environment
/// variable, or a temp file - which is what makes THREAT_MODEL.md T4 achievable with a native backend.
/// </remarks>
public sealed unsafe class PasswordProvider : ComObjectBase
{
    private readonly string _password;
    private readonly Action _onRequested;

    public PasswordProvider(string password, Action onRequested)
    {
        _password = password;
        _onRequested = onRequested;
    }

    protected override int MethodCount => 4;

    protected override bool Supports(in Guid iid) => iid == Iid.ICryptoGetTextPassword;

    protected override void FillVtable(nint* vtable) =>
        vtable[3] = (nint)(delegate* unmanaged<void*, nint*, int>)&CryptoGetTextPasswordThunk;

    [UnmanagedCallersOnly]
    private static int CryptoGetTextPasswordThunk(void* self, nint* password)
    {
        try
        {
            var instance = Resolve<PasswordProvider>(self);
            instance._onRequested();
            if (password is null)
            {
                return Hresult.E_INVALIDARG;
            }

            *password = BStr.Allocate(instance._password);
            return Hresult.S_OK;
        }
        catch
        {
            return Hresult.E_FAIL;
        }
    }
}

/// <summary>
/// <c>IArchiveOpenCallback</c>.
/// </summary>
/// <remarks>
/// <b>Finding.</b> A 7z archive written with encrypted headers (<c>-mhe=on</c>) cannot even be
/// <em>opened</em> without a password: <c>Open</c> returns <c>S_FALSE</c> when no open callback can
/// supply one. So the password prompt is not an extraction-time concern - <c>IArchiveSession</c> must be
/// able to request a password during <c>OpenAsync</c>, and <c>PasswordRequired</c> has to be a legal
/// outcome of opening, not only of extracting.
/// </remarks>
public sealed unsafe class OpenCallback : ComObjectBase
{
    private readonly string? _password;
    private PasswordProvider? _provider;

    public OpenCallback(string? password) => _password = password;

    public int PasswordRequests { get; private set; }

    protected override int MethodCount => 5;

    protected override bool Supports(in Guid iid) => iid == Iid.IArchiveOpenCallback;

    protected override nint QueryOtherInterface(in Guid iid)
    {
        if (iid != Iid.ICryptoGetTextPassword || _password is null)
        {
            return 0;
        }

        _provider ??= new PasswordProvider(_password, () => PasswordRequests++);
        return _provider.Pointer;
    }

    protected override void FillVtable(nint* vtable)
    {
        vtable[3] = (nint)(delegate* unmanaged<void*, ulong*, ulong*, int>)&SetTotalThunk;
        vtable[4] = (nint)(delegate* unmanaged<void*, ulong*, ulong*, int>)&SetCompletedThunk;
    }

    protected override void DisposeCore() => _provider?.Dispose();

    [UnmanagedCallersOnly]
    private static int SetTotalThunk(void* self, ulong* files, ulong* bytes) => Hresult.S_OK;

    [UnmanagedCallersOnly]
    private static int SetCompletedThunk(void* self, ulong* files, ulong* bytes) => Hresult.S_OK;
}
