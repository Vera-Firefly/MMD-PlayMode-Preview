using System;

[Serializable]
internal sealed class ConverterWindowContext
{
    [NonSerialized] private MainConverterWindow owner;

    internal MainConverterWindow Owner => owner;

    internal void Attach(MainConverterWindow window)
    {
        owner = window ?? throw new ArgumentNullException(nameof(window));
    }
}
