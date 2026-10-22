// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;

using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

// Land drawn past the rim of the map, taken round to the other side of it rather than flattened
// onto it. Three steps, because the graph has to be cut before the faces are traced, the faces
// have to exist before anyone can say which of them lie beyond, and the arcs along the cut have
// to be doubled only once the countries are assembled from them.
public interface IRimService
{
    // 4g: the antimeridian as two arcs of ArcKind.Rim, one down each edge of the map, traced
    // from the projection rather than taken from the drawn rim. 5a nodes them into everything
    // they cross and keeps only the stretches that lie over land.
    void AddArcs(ArcGraph arcGraph, Envelope sourceEnvelope);

    // 5i: gives every face beyond the rim the longitude offset that takes it round to the other
    // side, and takes out of the graph the ones that would land on ground already drawn there.
    // Returns those, which are faces left out like the ones 7a drops.
    IReadOnlyList<Face> Wrap(ArcGraph arcGraph, Envelope sourceEnvelope);

    // 7a': gives each wrapped shape its own copy of the rim arcs it is bounded by, since the
    // copy it shares with the shape inside the rim is written at the other longitude. Returns
    // the offset every arc is to be written with.
    IReadOnlyDictionary<Arc, double> Twin(ArcGraph arcGraph, IEnumerable<Country> countries);
}
