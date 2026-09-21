using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KidShell.Core.Mvvm;

/// <summary>
/// Small hand-rolled MVVM base. KidShell deliberately avoids pulling in an
/// MVVM framework for this: the surface used is two types wide.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
