namespace WinStudio.App.Models;

public sealed record WindowTargetOption(nint Handle, string Title)
{
    public override string ToString()
    {
        return Title;
    }
}
