// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;

using NetTopologySuite.Geometries;

// A point on the unit sphere as a vector in three dimensions. Distances, bisectors and
// circumcentres are all short formulas in this form and have no antimeridian and no pole to
// trip over, which longitude and latitude have both of.
internal readonly record struct SphereVector(double X, double Y, double Z)
{
    public double Length => Math.Sqrt(this.Dot(this));

    // Longitude and latitude in degrees.
    public static SphereVector FromGeographic(Coordinate coordinate)
    {
        double longitude = coordinate.X * (Math.PI / 180.0);
        double latitude = coordinate.Y * (Math.PI / 180.0);
        double cosLatitude = Math.Cos(latitude);

        return new SphereVector(
            cosLatitude * Math.Cos(longitude),
            cosLatitude * Math.Sin(longitude),
            Math.Sin(latitude));
    }

    // The point the fraction of the way along the great circle from one point to the other.
    public static SphereVector Interpolate(SphereVector from, SphereVector to, double fraction)
    {
        double angle = from.AngleTo(to);
        if (angle < 1e-15)
        {
            return from;
        }

        double sinAngle = Math.Sin(angle);

        return from.Scale(Math.Sin((1.0 - fraction) * angle) / sinAngle)
            .Add(to.Scale(Math.Sin(fraction * angle) / sinAngle));
    }

    public SphereVector Add(SphereVector other)
    {
        return new SphereVector(this.X + other.X, this.Y + other.Y, this.Z + other.Z);
    }

    // The angle between two points seen from the centre, which is their distance on the unit
    // sphere. The arctangent rather than the arccosine of the dot product, which loses half its
    // digits for points close together - and points a kilometre apart are what this is used on.
    public double AngleTo(SphereVector other)
    {
        return Math.Atan2(this.Cross(other).Length, this.Dot(other));
    }

    public SphereVector Cross(SphereVector other)
    {
        return new SphereVector(
            (this.Y * other.Z) - (this.Z * other.Y),
            (this.Z * other.X) - (this.X * other.Z),
            (this.X * other.Y) - (this.Y * other.X));
    }

    public double Dot(SphereVector other)
    {
        return (this.X * other.X) + (this.Y * other.Y) + (this.Z * other.Z);
    }

    public SphereVector Normalize()
    {
        double length = this.Length;

        return new SphereVector(this.X / length, this.Y / length, this.Z / length);
    }

    public SphereVector Scale(double factor)
    {
        return new SphereVector(this.X * factor, this.Y * factor, this.Z * factor);
    }

    public SphereVector Subtract(SphereVector other)
    {
        return new SphereVector(this.X - other.X, this.Y - other.Y, this.Z - other.Z);
    }

    public Coordinate ToGeographic()
    {
        return new Coordinate(
            Math.Atan2(this.Y, this.X) * (180.0 / Math.PI),
            Math.Asin(Math.Clamp(this.Z, -1.0, 1.0)) * (180.0 / Math.PI));
    }
}
