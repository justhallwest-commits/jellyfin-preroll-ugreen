using System;

namespace JellyfinPreroll;

public sealed class SelectionContext
{
	public Guid UserId { get; set; }

	public DateTime AtUtc { get; set; } = DateTime.UtcNow;

	public bool DryRun { get; set; }

	/// <summary>
	/// Picking for an item further down a play queue (it may never be reached). Shuffle reserves the
	/// clip so the queue doesn't repeat one, but it only counts as "had its turn" once it actually plays.
	/// </summary>
	public bool PlanAhead { get; set; }

	public bool IgnoreCooldown { get; set; }

	public bool HasLastPrerollOverride { get; set; }

	public DateTime? LastPrerollUtc { get; set; }
}
