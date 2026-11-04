namespace Nexus.App.Widgets;

/// <summary>
/// Implemented by views whose size can change on their own (not because of an activity update), such as Island
/// Center when apps appear in the volume mixer. The presenter re-measures and re-morphs the island on the event.
/// </summary>
public interface IResizableView
{
    event EventHandler? ContentResized;
}
