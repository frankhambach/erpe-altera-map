// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

using System.Collections.Immutable;

// A region enclosed by arcs. Owner is null until face assignment runs, and stays null
// for the ocean and for any region no country claims.
public sealed class Face(
    int id,
    ImmutableArray<DirectedArc> shell,
    ImmutableArray<ImmutableArray<DirectedArc>> holes)
{
    public ImmutableArray<ImmutableArray<DirectedArc>> Holes { get; } = holes;

    public int Id { get; } = id;

    // What 7b adds to every longitude of the face: -360 for a face drawn beyond the eastern
    // rim, +360 for one beyond the western, and 0 for the rest, which is nearly all of them.
    // Set by IRimService.Wrap once the faces are traced.
    public double LongitudeOffset { get; set; }

    public Country? Owner { get; set; }

    public ImmutableArray<DirectedArc> Shell { get; } = shell;
}
