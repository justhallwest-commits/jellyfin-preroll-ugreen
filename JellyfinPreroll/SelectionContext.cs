using System;

namespace JellyfinPreroll;

public sealed class SelectionContext
{
	public Guid UserId { get; set; }

	public DateTime AtUtc { get; set; } = DateTime.UtcNow;

	public bool DryRun { get; set; }

	public bool IgnoreCooldown { get; set; }

	public bool HasLastPrerollOverride { get; set; }

	public DateTime? LastPrerollUtc { get; set; }
}
