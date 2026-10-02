// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using System.Collections.Generic;

using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

public interface ICorrectionService
{
    IEnumerable<Gap> FindGaps(IList<Country> countries);

    void Snap(IEnumerable<Coordinate> coordinates, double snapTolerance);

    void Weld(ArcGraph arcGraph, double weldTolerance);
}