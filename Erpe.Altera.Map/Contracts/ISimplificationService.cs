// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;

using NetTopologySuite.Geometries;

public interface ISimplificationService
{
    // Which of a line's interior coordinates simplification removes at this tolerance. The
    // two ends are never removed.
    //
    // It answers with the coordinates to drop rather than the line to keep, because the
    // caller asks this once per arc and then drops those coordinates wherever they appear.
    // That is what keeps a border identical on both sides: the decision is made once, for
    // the arc, and not twice, once for each country that happens to be built from it.
    //
    // tolerance is the effective area of a vertex, as Visvalingam measures it, in the units
    // of the coordinates. minimumCount is how many points of the line to keep whatever the
    // tolerance says.
    IEnumerable<Coordinate> SelectRemoved(Coordinate[] line, double tolerance, int minimumCount);
}
