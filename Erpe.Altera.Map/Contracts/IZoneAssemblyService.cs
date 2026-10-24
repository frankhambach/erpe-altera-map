// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;

using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

// The sea as a graph of its own and each country's economic zone assembled from its faces. The
// graph is the zone boundaries cut back to the water, the shores they end on and the antimeridian,
// and the shores and the rim over land are the land graph's own arcs, shared and never changed, so
// that a zone and the land beside it are bounded by the very same coordinates.
public interface IZoneAssemblyService
{
    // One country per country with a zone, carrying the land country's identifier and holding its
    // zone as shapes in the source map's coordinates. The faces of the sea graph are left behind it,
    // each with the zone it belongs to as its owner, or none for the high seas.
    IReadOnlyList<Country> Assemble(
        ZoneGraph zones,
        ArcGraph arcGraph,
        IEnumerable<Shore> shores,
        Envelope sourceEnvelope);
}
