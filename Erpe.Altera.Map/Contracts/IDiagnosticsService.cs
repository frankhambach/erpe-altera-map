// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Erpe.Altera.Map.Models;

// Writes the intermediate stages of the pipeline back out as SVG, in the source map's own
// coordinates, so that a stage can be opened next to the input it came from.
public interface IDiagnosticsService
{
    Task WriteArcsAsync(IEnumerable<Arc> arcs, FileInfo file);

    Task WriteFacesAsync(IEnumerable<Face> faces, FileInfo file);
}
