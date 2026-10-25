// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;

using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

public interface IVerificationService
{
    // The acceptance test for the whole pipeline: that a border exists once and both
    // countries either side of it are built from the same coordinates.
    //
    // Counted as a planar partition. Every segment inland should be used exactly twice,
    // once from each side and in opposite directions, and the segments used once should be
    // the outer boundary of the whole - sea coast and lake shore.
    //
    // coastLines are the sea coast and lake shore arcs, in the same coordinates as the
    // countries. Every segment used once has to be one of theirs, and that is the only part
    // of this with teeth: a layer whose borders are not shared at all still has every
    // country ring closed and still has no segment used three times, because a ring is a
    // closed loop whichever coordinates it is made of. Pass nothing for a layer that does
    // not cover the land, which has no coast to be held to.
    SharingReport VerifySharing(IEnumerable<Country> countries, IEnumerable<Coordinate[]>? coastLines);

    // That the land and the economic zones do not overlap: a point inside every shape of either is
    // inside nothing of the other. What VerifySharing cannot see, since an island a zone has no hole
    // for still has every segment of its coast used once, as a coast should be. Answers how many
    // shapes lie inside one of the other kind.
    int VerifyDisjoint(IEnumerable<Country> land, IEnumerable<Country> zones);
}
