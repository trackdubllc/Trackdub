namespace DubBench.ViewModels;

public interface ITabViewModel
{
    string Title { get; }
    string IconGlyph { get; }
    bool IsSelected { get; set; }
}
