// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using NetTopologySuite.Geometries;

public interface ISimplificationService
{
    Polygon Simplify(Polygon polygon, double tolerance);
}