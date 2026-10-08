// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

// Writes the partition as a topology: the arcs named once, and every country referring to them
// by index. Several layers go into one file, because sharing the arcs between them is what the
// format is for - a national border is also the edge of a substate, and in a topology that is
// one arc with two references rather than two copies of a line.
public interface ITopoJsonService
{
    // arcs is each arc's coordinates in the coordinates the file is written in, already
    // simplified where the output is a coarse one. The countries' rings name those same arcs, so
    // simplifying an arc simplifies it for everything that refers to it at once.
    Task WriteAsync(
        IReadOnlyList<(string Name, IReadOnlyList<Country> Countries)> layers,
        IReadOnlyDictionary<Arc, Coordinate[]> arcs,
        FileInfo file);
}
