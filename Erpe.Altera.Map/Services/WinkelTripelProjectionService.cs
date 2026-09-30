// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;

using Erpe.Altera.Map.Contracts;

using NetTopologySuite.Geometries;

using Serilog;

public sealed class WinkelTripelProjectionService : IProjectionService
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

    /// <summary>
    /// Rescales the given coordinate from the source envelope to the Winkel Tripel projection envelope.
    /// </summary>
    /// <param name="coordinate">The coordinate to be rescaled.</param>
    /// <param name="sourceEnvelope">The source envelope from which the coordinate is rescaled.</param>
    /// <returns>A new coordinate rescaled to the Winkel Tripel projection envelope.</returns>
    public Coordinate Rescale(Coordinate coordinate, Envelope sourceEnvelope)
    {
        ArgumentNullException.ThrowIfNull(coordinate);
        ArgumentNullException.ThrowIfNull(sourceEnvelope);

        double x = WinkelTripelEnvelope.MinX
            + (((coordinate.X - sourceEnvelope.MinX) / (sourceEnvelope.MaxX - sourceEnvelope.MinX))
                * (WinkelTripelEnvelope.MaxX - WinkelTripelEnvelope.MinX));
        double y = -(WinkelTripelEnvelope.MinY
            + (((coordinate.Y - sourceEnvelope.MinY) / (sourceEnvelope.MaxY - sourceEnvelope.MinY))
                * (WinkelTripelEnvelope.MaxY - WinkelTripelEnvelope.MinY)));
        return new Coordinate(x, y);
    }

    public Coordinate Unproject(Coordinate coordinate)
    {
        ArgumentNullException.ThrowIfNull(coordinate);

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

            if (Math.Abs(sinAlphaSquared) < Epsilon)
            {
                Log.Error("Denominator is zero.");
                return new Coordinate(
                    this.geometryFactory.PrecisionModel.MakePrecise(lambda),
                    this.geometryFactory.PrecisionModel.MakePrecise(phi));
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

                return this.CreateCoordinate(lambda, phi);
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
        return this.CreateCoordinate(lambda, phi);
    }

    private Coordinate CreateCoordinate(double lambda, double phi)
    {
        double x = (lambda / Math.PI) * 180.0;
        double y = (phi / Math.PI) * 180.0;
        if (x > 180.0)
        {
            x = 180.0;
        }

        if (x < -180.0)
        {
            x = -180.0;
        }

        return new Coordinate(
            this.geometryFactory.PrecisionModel.MakePrecise(x),
            this.geometryFactory.PrecisionModel.MakePrecise(y));
    }
}