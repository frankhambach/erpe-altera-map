// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;

using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

// The exclusive economic zones: the water within 200 nautical miles of a country's coast or lake
// shore that is nearer to it than to any other country's. Measured on the sphere and not in any
// projection, because a distance in the source map's units varies by a factor of ten with
// latitude and a distance in degrees is not a distance at all.
public interface IEconomicZoneService
{
    // The median lines between the zones and the outer limit of each, in longitude and latitude.
    // Both are computed over land as well as water, and cutting them back to the water is left to
    // whoever puts them into a graph.
    IReadOnlyList<ZoneBoundary> GetBoundaries(IEnumerable<Shore> shores, Envelope sourceEnvelope);

    // The boundaries in the source map's coordinates, cut back to the water, as arcs of
    // ArcKind.Median and ArcKind.Limit in a graph of their own. It mints its identifiers from the
    // land graph's, and a median line ends at the land graph's own node where its two countries'
    // shores meet, so the two graphs share those nodes and no coast arc is split.
    ArcGraph AddArcs(
        ArcGraph arcGraph,
        IEnumerable<Shore> shores,
        IEnumerable<ZoneBoundary> boundaries,
        Envelope sourceEnvelope);
}
