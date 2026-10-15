namespace WheelSwap.Core;

public sealed class SwapLog
{
    public event Action<string, bool>? Line;   // text, isWarning
    public List<string> Warnings { get; } = [];
    public List<string> All { get; } = [];

    public void Info(string s) { All.Add(s); Line?.Invoke(s, false); }
    public void Warn(string s) { All.Add("WARNING: " + s); Warnings.Add(s); Line?.Invoke(s, true); }
}
