internal sealed class VmdCameraPage
{
    internal void Draw(ConverterWindowContext context)
    {
        context.Owner.DrawVmdCameraPage();
    }

    internal void DrawAction(ConverterWindowContext context)
    {
        context.Owner.DrawCurrentPageAction();
    }
}
