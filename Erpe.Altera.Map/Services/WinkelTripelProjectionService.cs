// <copyright file="WinkelTripelProjectionService.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

namespace Erpe.Altera.Map.Services;

using System;
using System.Linq;

using Erpe.Altera.Map.Contracts;

using NetTopologySuite.Geometries;

using Serilog;

public class WinkelTripelProjectionService : IProjectionService
{
    private const double Epsilon = 1e-12;

    private const int MaximumIterationCount = 100;

    private static readonly Envelope WinkelTripelEnvelope = new Envelope(
        -(Math.PI + 2) / 2,
        (Math.PI + 2) / 2,
        -Math.PI / 2,
        Math.PI / 2);

    private readonly GeometryFactory geometryFactory;

    public WinkelTripelProjectionService(GeometryFactory geometryFactory)
    {
        this.geometryFactory = geometryFactory;
    }

    public LineString Unproject(CoordinateSequence coordinateSequence, Envelope sourceEnvelope)
    {
        return this.geometryFactory.CreateLineString(
            Enumerable.Range(0, coordinateSequence.Count)
                .Select(coordinateSequence.GetCoordinate)
                .Select(coordinate => this.Unproject(Transform(coordinate, sourceEnvelope)))
                .ToArray());
    }

    private static Coordinate Transform(Coordinate coordinate, Envelope sourceEnvelope)
    {
        double x = WinkelTripelEnvelope.MinX
            + (((coordinate.X - sourceEnvelope.MinX) / (sourceEnvelope.MaxX - sourceEnvelope.MinX))
                * (WinkelTripelEnvelope.MaxX - WinkelTripelEnvelope.MinX));
        double y = -(WinkelTripelEnvelope.MinY
            + (((coordinate.Y - sourceEnvelope.MinY) / (sourceEnvelope.MaxY - sourceEnvelope.MinY))
                * (WinkelTripelEnvelope.MaxY - WinkelTripelEnvelope.MinY)));
        return new Coordinate(x, y);
    }

    private Coordinate Unproject(Coordinate coordinate)
    {
        const double cosPhi1 = 2 / Math.PI;

        double sinHalfLambda, cosHalfLambda, sinPhi, cosPhi;
        double alpha, sinAlpha, sinAlphaSquared, cosAlpha;

        double deltaLambda, deltaPhi;
        double x, y, determinant;
        double distanceX, distanceY;
        double derivativeXByLambda, derivativeYByLambda, derivativeXByPhi, derivativeYByPhi;

        if ((Math.Abs(coordinate.X) < Epsilon) && (Math.Abs(coordinate.Y) < Epsilon))
        {
            return new Coordinate(0.0, 0.0);
        }

        double phi = coordinate.Y;
        double lambda = coordinate.X;

        for (int index = 0; index < MaximumIterationCount; ++index)
        {
            sinHalfLambda = Math.Sin(lambda * 0.5);
            cosHalfLambda = Math.Cos(lambda * 0.5);
            sinPhi = Math.Sin(phi);
            cosPhi = Math.Cos(phi);

            cosAlpha = cosPhi * cosHalfLambda;
            alpha = Math.Acos(cosAlpha);
            sinAlphaSquared = 1 - (cosAlpha * cosAlpha);
            sinAlpha = Math.Sqrt(sinAlphaSquared);

            if (sinAlphaSquared == 0.0)
            {
                Log.Error("Denominator is zero.");
                return new Coordinate(lambda, phi);
            }

            x = 0.5 * (((2.0 * cosPhi * sinHalfLambda * alpha) / sinAlpha) + (lambda * cosPhi1));
            y = 0.5 * (((alpha * sinPhi) / sinAlpha) + phi);

            distanceX = x - coordinate.X;
            distanceY = y - coordinate.Y;
            if ((Math.Abs(distanceX) < Epsilon) && (Math.Abs(distanceY) < Epsilon))
            {
                if (phi > (Math.PI / 2.0))
                {
                    phi -= 2.0 * (phi - (Math.PI / 2.0));
                }

                if (phi < (-Math.PI / 2.0))
                {
                    phi -= 2.0 * (phi + (Math.PI / 2.0));
                }

                return new Coordinate((lambda / Math.PI) * 180.0, (phi / Math.PI) * 180.0);
            }

            derivativeXByPhi = ((sinHalfLambda * cosHalfLambda * sinPhi * cosPhi)
                    - ((alpha * sinPhi * sinHalfLambda) / sinAlpha))
                / sinAlphaSquared;
            derivativeXByLambda = 0.5
                * ((((cosPhi * cosPhi * sinHalfLambda * sinHalfLambda)
                            + ((alpha * cosPhi * cosHalfLambda * sinPhi * sinPhi) / sinAlpha))
                        / sinAlphaSquared)
                    + cosPhi1);
            derivativeYByPhi = 0.5
                * ((((sinPhi * sinPhi * cosHalfLambda) + ((alpha * sinHalfLambda * sinHalfLambda * cosPhi) / sinAlpha))
                        / sinAlphaSquared)
                    + 1.0);
            derivativeYByLambda = (0.25
                    * ((sinPhi * cosPhi * sinHalfLambda)
                        - ((alpha * sinPhi * cosPhi * cosPhi * sinHalfLambda * cosHalfLambda) / sinAlpha)))
                / sinAlphaSquared;

            determinant = (derivativeXByPhi * derivativeYByLambda) - (derivativeYByPhi * derivativeXByLambda);

            deltaLambda = ((distanceY * derivativeXByPhi) - (distanceX * derivativeYByPhi)) / determinant;
            deltaPhi = ((distanceX * derivativeYByLambda) - (distanceY * derivativeXByLambda)) / determinant;

            phi -= deltaPhi;
            lambda -= deltaLambda;
        }

        Log.Warning(
            "Could not accurately determine coordinates for projected point {Point} after {IterationCount} iterations.",
            coordinate,
            MaximumIterationCount);
        return new Coordinate(
            this.geometryFactory.PrecisionModel.MakePrecise((lambda / Math.PI) * 180.0),
            this.geometryFactory.PrecisionModel.MakePrecise((phi / Math.PI) * 180.0));
    }
}