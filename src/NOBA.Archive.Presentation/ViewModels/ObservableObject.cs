using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NOBA.Archive.Presentation.ViewModels;

/// <summary>
/// The smallest <see cref="INotifyPropertyChanged"/> base that does the job.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taken from an MVVM toolkit, for the same reason the interop layer was:
/// every dependency added here has to be restored on the build machine and justified in
/// <c>DEPENDENCIES.md</c>, and this is thirty lines. A source generator would save typing and cost a
/// package.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Assigns and notifies only on a real change. Returns whether anything changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(propertyName);
        return true;
    }
}
