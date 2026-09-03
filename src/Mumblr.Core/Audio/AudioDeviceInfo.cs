namespace Mumblr.Core.Audio;

/// <summary>A selectable capture device. <see cref="Id"/> is stable across reboots.</summary>
public sealed record AudioDeviceInfo(string Id, string Name)
{
    public override string ToString() => Name;
}
