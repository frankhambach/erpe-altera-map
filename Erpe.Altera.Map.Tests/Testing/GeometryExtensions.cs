// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Tests.Testing;

using NetTopologySuite.Geometries;

internal static class GeometryExtensions
{
    public static GeometryAssertions Should(this Geometry geometry)
    {
        return new GeometryAssertions(geometry);
    }
}