using System;
using System.Collections.Generic;

namespace JellyfinPreroll;

public sealed class MediaFacts
{
	public HashSet<string> Tags { get; } = NewSet();

	public HashSet<string> Genres { get; } = NewSet();

	public HashSet<string> Studios { get; } = NewSet();

	public HashSet<string> Ratings { get; } = NewSet();

	public HashSet<string> Libraries { get; } = NewSet();

	public HashSet<string> Titles { get; } = NewSet();

	public HashSet<string> Types { get; } = NewSet();

	public HashSet<string> For(MatchField field)
	{
		return field switch
		{
			MatchField.Genre => Genres, 
			MatchField.Studio => Studios, 
			MatchField.Rating => Ratings, 
			MatchField.Library => Libraries, 
			MatchField.Title => Titles, 
			MatchField.Type => Types, 
			_ => Tags, 
		};
	}

	private static HashSet<string> NewSet()
	{
		return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	}
}
