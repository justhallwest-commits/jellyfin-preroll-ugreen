namespace JellyfinPreroll;

public class SequenceSlot
{
	public SlotKind Kind { get; set; }

	public string ClipTag { get; set; } = string.Empty;

	public SlotAppliesTo AppliesTo { get; set; }
}
