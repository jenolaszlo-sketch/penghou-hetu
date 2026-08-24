namespace Penghou.Hetu;

[Flags]
public enum CodeGraphCapabilities
{
    None = 0,
    Syntax = 1 << 0,
    Symbols = 1 << 1,
    References = 1 << 2,
    Calls = 1 << 3,
    Types = 1 << 4,
    Inheritance = 1 << 5,
    Imports = 1 << 6,
    DataFlow = 1 << 7
}
