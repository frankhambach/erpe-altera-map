// <copyright file="SvgApproximationService.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

using Erpe.Altera.Map.Contracts;

using NetTopologySuite.Geometries;

using Serilog;

using Svg;
using Svg.Pathing;

public class SvgApproximationService : ISvgApproximationService
{
    private const double Tolerance = 0.01;

    private readonly CoordinateSequenceFactory coordinateSequenceFactory;

    public SvgApproximationService(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        this.coordinateSequenceFactory = coordinateSequenceFactory;
    }

    public IEnumerable<CoordinateSequence> Approximate(SvgElement element)
    {
        return element switch
            {
                SvgPath path => this.ApproximatePath(path),
                SvgPolygon polygon => [this.ConvertPolygon(polygon)],
                SvgRectangle rectangle => [this.ConvertRectangle(rectangle)],
                _ => Enumerable.Empty<CoordinateSequence>(),
            };
    }

    public IEnumerable<Coordinate> GetCoordinates(SvgPath path)
    {
        return ConvertToAbsolute(path).Select(pathSegment => pathSegment.End.ToCoordinate());
    }

    private static IEnumerable<Coordinate> ApproximateClosedPath(IList<SvgPathSegment> shape)
    {
        Coordinate firstPoint = shape[0].End.ToCoordinate();
        Coordinate startPoint = new Coordinate();
        SvgPathSegment? previousPathSegment = null;
        foreach (SvgPathSegment pathSegment in shape)
        {
            Coordinate endPoint = pathSegment.End.ToCoordinate();
            switch (pathSegment)
            {
                case SvgCubicCurveSegment cubicCurveSegment:
                    foreach (Coordinate point in ApproximateCubicBezier(
                                 startPoint,
                                 cubicCurveSegment,
                                 previousPathSegment as SvgCubicCurveSegment))
                    {
                        yield return point;
                    }

                    break;
                case SvgClosePathSegment:
                    yield return firstPoint;
                    break;
                default:
                    yield return endPoint;
                    break;
            }

            startPoint = endPoint;
            previousPathSegment = pathSegment;
        }
    }

    private static IEnumerable<Coordinate> ApproximateCubicBezier(
        Coordinate startPoint,
        SvgCubicCurveSegment cubicCurveSegment,
        SvgCubicCurveSegment? previousCubicCurveSegment)
    {
        Coordinate firstControlPoint = cubicCurveSegment.FirstControlPoint.ToCoordinate();
        Coordinate secondControlPoint = cubicCurveSegment.SecondControlPoint.ToCoordinate();
        if (double.IsNaN(firstControlPoint.X) || double.IsNaN(firstControlPoint.Y))
        {
            firstControlPoint = previousCubicCurveSegment == null
                ? startPoint
                : Reflect(previousCubicCurveSegment.SecondControlPoint.ToCoordinate(), startPoint);
        }

        Coordinate endPoint = cubicCurveSegment.End.ToCoordinate();
        return ApproximateCubicBezier(startPoint, firstControlPoint, secondControlPoint, endPoint);
    }

    private static IEnumerable<Coordinate> ApproximateCubicBezier(
        Coordinate startPoint,
        Coordinate firstControlPoint,
        Coordinate secondControlPoint,
        Coordinate endPoint)
    {
        if (IsSufficientlyFlat(startPoint, firstControlPoint, secondControlPoint, endPoint))
        {
            Log.Debug("Approximating with line");
            yield return endPoint;
            yield break;
        }

        // Calculate midpoints of the line segments
        Coordinate mid1 = new Coordinate(
            (startPoint.X + firstControlPoint.X) / 2,
            (startPoint.Y + firstControlPoint.Y) / 2);
        Coordinate mid2 = new Coordinate(
            (firstControlPoint.X + secondControlPoint.X) / 2,
            (firstControlPoint.Y + secondControlPoint.Y) / 2);
        Coordinate mid3 = new Coordinate(
            (secondControlPoint.X + endPoint.X) / 2,
            (secondControlPoint.Y + endPoint.Y) / 2);

        // Calculate midpoints of the new line segments
        Coordinate mid12 = new Coordinate((mid1.X + mid2.X) / 2, (mid1.Y + mid2.Y) / 2);
        Coordinate mid23 = new Coordinate((mid2.X + mid3.X) / 2, (mid2.Y + mid3.Y) / 2);

        Coordinate mid123 = new Coordinate((mid12.X + mid23.X) / 2, (mid12.Y + mid23.Y) / 2);

        // Calculate the sub-segment length
        double segmentLength = GetLength(startPoint, endPoint);
        Log.Debug(
            "Approximating cubic curve between {Start} and {End} with length {Length}",
            startPoint,
            endPoint,
            segmentLength);

        foreach (Coordinate point in ApproximateCubicBezier(startPoint, mid1, mid12, mid123)
                     .Concat(ApproximateCubicBezier(mid123, mid23, mid3, endPoint)))
        {
            yield return point;
        }
    }

    private static SvgPathSegmentList ConvertToAbsolute(SvgPath path)
    {
        PointF startPoint = PointF.Empty;
        SvgPathSegmentList result = new SvgPathSegmentList();
        foreach (SvgPathSegment pathSegment in path.PathData)
        {
            PointF endPoint = ConvertToAbsolute(pathSegment.End, pathSegment.IsRelative, startPoint);
            switch (pathSegment)
            {
                case SvgCubicCurveSegment cubicCurveSegment:
                    PointF firstControlPoint = cubicCurveSegment.FirstControlPoint;
                    if (!float.IsNaN(cubicCurveSegment.FirstControlPoint.X)
                        || !float.IsNaN(cubicCurveSegment.FirstControlPoint.Y))
                    {
                        firstControlPoint = ConvertToAbsolute(
                            cubicCurveSegment.FirstControlPoint,
                            cubicCurveSegment.IsRelative,
                            startPoint);
                    }

                    result.Add(
                        new SvgCubicCurveSegment(
                            false,
                            firstControlPoint,
                            ConvertToAbsolute(
                                cubicCurveSegment.SecondControlPoint,
                                cubicCurveSegment.IsRelative,
                                startPoint),
                            endPoint));
                    break;
                case SvgLineSegment:
                    result.Add(new SvgLineSegment(false, endPoint));
                    break;
                case SvgMoveToSegment:
                    result.Add(new SvgMoveToSegment(false, endPoint));
                    break;
                default:
                    result.Add(pathSegment);
                    break;
            }

            startPoint = endPoint;
        }

        return result;
    }

    private static PointF ConvertToAbsolute(PointF point, bool isRelative, PointF previousPoint)
    {
        if (float.IsNaN(point.X))
        {
            point.X = previousPoint.X;
        }
        else if (isRelative)
        {
            point.X += previousPoint.X;
        }

        if (float.IsNaN(point.Y))
        {
            point.Y = previousPoint.Y;
        }
        else if (isRelative)
        {
            point.Y += previousPoint.Y;
        }

        return point;
    }

    private static double GetLength(Coordinate startPoint, Coordinate endPoint)
    {
        return Math.Sqrt(Math.Pow(endPoint.X - startPoint.X, 2) + Math.Pow(endPoint.Y - startPoint.Y, 2));
    }

    private static bool IsSufficientlyFlat(
        Coordinate startPoint,
        Coordinate firstControlPoint,
        Coordinate secondControlPoint,
        Coordinate endPoint)
    {
        double ux = (3.0 * firstControlPoint.X) - (2.0 * startPoint.X) - endPoint.X;
        ux *= ux;
        double uy = (3.0 * firstControlPoint.Y) - (2.0 * startPoint.Y) - endPoint.Y;
        uy *= uy;
        double vx = (3.0 * secondControlPoint.X) - (2.0 * endPoint.X) - startPoint.X;
        vx *= vx;
        double vy = (3.0 * secondControlPoint.Y) - (2.0 * endPoint.Y) - startPoint.Y;
        vy *= vy;
        if (ux < vx)
        {
            ux = vx;
        }

        if (uy < vy)
        {
            uy = vy;
        }

        return (ux + uy) <= Tolerance;
    }

    private static Coordinate Reflect(Coordinate point, Coordinate mirror)
    {
        double distanceX = Math.Abs(mirror.X - point.X);
        double distanceY = Math.Abs(mirror.Y - point.Y);
        return new Coordinate(
            mirror.X + (mirror.X >= point.X ? distanceX : -distanceX),
            mirror.Y + (mirror.Y >= point.Y ? distanceY : -distanceY));
    }

    private IEnumerable<CoordinateSequence> ApproximatePath(SvgPath path)
    {
        return ConvertToAbsolute(path)
            .ChunkBy(pathSegment => pathSegment is SvgMoveToSegment)
            .Select(ApproximateClosedPath)
            .Select(points => points.ToArray())
            .Where(coordinates => coordinates.Length >= 2)
            .Select(coordinates => this.coordinateSequenceFactory.Create(coordinates));
    }

    private CoordinateSequence ConvertPolygon(SvgPolygon polygon)
    {
        return this.coordinateSequenceFactory.Create(
            EnumerableExtensions.Chunk(polygon.Points, 2)
                .Select(points => points.ToArray())
                .Select(points => new Coordinate(points[0], points[1]))
                .ToArray());
    }

    private CoordinateSequence ConvertRectangle(SvgRectangle rectangle)
    {
        return this.coordinateSequenceFactory.Create(
            [
                new Coordinate(rectangle.X.Value, rectangle.Y.Value),
                new Coordinate(rectangle.X.Value + rectangle.Width, rectangle.Y.Value),
                new Coordinate(rectangle.X.Value + rectangle.Width, rectangle.Y.Value + rectangle.Height),
                new Coordinate(rectangle.X.Value, rectangle.Y.Value + rectangle.Height),
            ]);
    }
}