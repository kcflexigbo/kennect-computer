namespace KennectComputer.Host.Windows;

/// <summary>Reports whether the interactive desktop is locked.</summary>
public interface IDesktopState
{
    bool IsLocked();
}
