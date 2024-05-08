// <copyright file="ValidationExtensions.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

namespace Erpe.Altera.Map;

using System.Linq;

using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Polygonize;

public static class ValidationExtensions
{
    public static Geometry RemoveSelfIntersections(this Polygon polygon)
    {
        if (polygon.IsValid)
        {
            return polygon;
        }

        Geometry exteriorGeometry = polygon.ExteriorRing.RemoveSelfIntersections();
        return polygon.InteriorRings.Select(interiorRing => interiorRing.RemoveSelfIntersections())
            .Aggregate(exteriorGeometry, (geometry, interiorGeometry) => geometry.Difference(interiorGeometry));
    }

    private static Geometry RemoveSelfIntersections(this LineString lineString)
    {
        Polygonizer polygonizer = new Polygonizer();
        if (lineString is LinearRing)
        {
            lineString = lineString.Factory.CreateLineString(lineString.CoordinateSequence);
        }

        Point point = lineString.Factory.CreatePoint(lineString.GetCoordinateN(0));
        polygonizer.Add(lineString.Union(point));
        return polygonizer.GetPolygons()
            .Aggregate((Geometry)Polygon.Empty, (firstPolygon, secondPolygon) => firstPolygon.Union(secondPolygon));
    }
}