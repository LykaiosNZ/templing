using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Songler.Components.Charts;

/// <summary>Renders an SVG &lt;text&gt; element (Razor reserves the &lt;text&gt; tag).</summary>
public class SvgText : ComponentBase
{
    [Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? Attrs { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenElement(0, "text");
        b.AddMultipleAttributes(1, Attrs);
        b.AddContent(2, ChildContent);
        b.CloseElement();
    }
}
