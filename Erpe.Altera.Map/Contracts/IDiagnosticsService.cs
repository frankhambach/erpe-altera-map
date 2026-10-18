// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

// Writes the intermediate stages of the pipeline back out as SVG, in the source map's own
// coordinates, so that a stage can be opened next to the input it came from.
public interface IDiagnosticsService
{
    // The rim and the view box come from the source document, so that a file opens on top of
    // the map it was read from: same coordinates, same framing, same whitespace around the rim.
    // A null view falls back to the contents' own extent, which is all a test has.
    Task WriteArcsAsync(
        IEnumerable<Arc> arcs,
        IEnumerable<CoordinateSequence> rim,
        Envelope? view,
        FileInfo file);

    Task WriteFacesAsync(IEnumerable<Face> faces, Envelope? view, FileInfo file);
}
