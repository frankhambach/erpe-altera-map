// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;

using Erpe.Altera.Map.Models;

public interface IVerificationService
{
    // The acceptance test for the whole pipeline: that a border exists once and both
    // countries either side of it are built from the same coordinates.
    //
    // Counted as a planar partition. Every segment inland should be used exactly twice,
    // once from each side and in opposite directions, and the segments used once should be
    // the outer boundary of the whole - sea coast and lake shore.
    //
    // coversTheLand says whether this layer is meant to cover everything between the
    // coasts, which only the national one is. When it does, the segments used once are
    // compared against the coast and lake arcs of the graph, and that comparison is the
    // only part of this with teeth: a layer whose borders are not shared at all still has
    // every country ring closed and still has no segment used three times, because a ring
    // is a closed loop whichever coordinates it is made of.
    SharingReport VerifySharing(IEnumerable<Country> countries, ArcGraph arcGraph, bool coversTheLand);
}
