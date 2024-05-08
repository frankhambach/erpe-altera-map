// <copyright file="ApproximationExtensions.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

namespace Erpe.Altera.Map;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

using NetTopologySuite.Geometries;

using Serilog;

using Svg;
using Svg.Pathing;

using Point = NetTopologySuite.Geometries.Point;

public static class ApproximationExtensions
{
    private const double Tolerance = 0.01;

    public static SvgPathSegmentList ToAbsolute(this SvgPathSegmentList pathSegmentList)
    {
        PointF startPoint = PointF.Empty;
        SvgPathSegmentList result = new SvgPathSegmentList();
        foreach (SvgPathSegment pathSegment in pathSegmentList)
        {
            PointF endPoint = pathSegment.End.ToAbsolute(pathSegment.IsRelative, startPoint);
            switch (pathSegment)
            {
                case SvgCubicCurveSegment cubicCurveSegment:
                    result.Add(
                        new SvgCubicCurveSegment(
                            false,
                            cubicCurveSegment.FirstControlPoint is { X: float.NaN, Y: float.NaN }
                                ? cubicCurveSegment.FirstControlPoint
                                : cubicCurveSegment.FirstControlPoint.ToAbsolute(
                                    cubicCurveSegment.IsRelative,
                                    startPoint),
                            cubicCurveSegment.SecondControlPoint.ToAbsolute(cubicCurveSegment.IsRelative, startPoint),
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

    public static IEnumerable<LinearRing> ToLinearRings(this SvgElement element)
    {
        return element switch
            {
                SvgPath path => path.ToLinearRings(),
                SvgPolygon polygon => new[] { polygon.ToLinearRing() },
                SvgRectangle rectangle => new[] { rectangle.ToLinearRing() },
                _ => Enumerable.Empty<LinearRing>(),
            };
    }

    public static Point ToPoint(this PointF point)
    {
        return new Point(point.X, point.Y);
    }

    private static IEnumerable<Point> ApproximateClosedPath(IList<SvgPathSegment> shape)
    {
        Point firstPoint = shape[0].End.ToPoint();
        Point startPoint = Point.Empty;
        SvgPathSegment? previousPathSegment = null;
        foreach (SvgPathSegment pathSegment in shape)
        {
            Point endPoint = pathSegment.End.ToPoint();
            switch (pathSegment)
            {
                case SvgCubicCurveSegment cubicCurveSegment:
                    foreach (Point point in ApproximateCubicBezier(
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

    private static IEnumerable<Point> ApproximateCubicBezier(
        Point startPoint,
        SvgCubicCurveSegment cubicCurveSegment,
        SvgCubicCurveSegment? previousCubicCurveSegment)
    {
        Point firstControlPoint = cubicCurveSegment.FirstControlPoint.ToPoint();
        Point secondControlPoint = cubicCurveSegment.SecondControlPoint.ToPoint();
        if (double.IsNaN(firstControlPoint.X) || double.IsNaN(firstControlPoint.Y))
        {
            firstControlPoint = previousCubicCurveSegment == null
                ? startPoint
                : Reflect(previousCubicCurveSegment.SecondControlPoint.ToPoint(), startPoint);
        }

        Point endPoint = cubicCurveSegment.End.ToPoint();
        return ApproximateCubicBezier(startPoint, firstControlPoint, secondControlPoint, endPoint);
    }

    private static IEnumerable<Point> ApproximateCubicBezier(
        Point startPoint,
        Point firstControlPoint,
        Point secondControlPoint,
        Point endPoint)
    {
        if (IsSufficientlyFlat(startPoint, firstControlPoint, secondControlPoint, endPoint))
        {
            Log.Debug("Approximating with line");
            yield return endPoint;
            yield break;
        }

        // Calculate midpoints of the line segments
        Point mid1 = new Point((startPoint.X + firstControlPoint.X) / 2, (startPoint.Y + firstControlPoint.Y) / 2);
        Point mid2 = new Point(
            (firstControlPoint.X + secondControlPoint.X) / 2,
            (firstControlPoint.Y + secondControlPoint.Y) / 2);
        Point mid3 = new Point((secondControlPoint.X + endPoint.X) / 2, (secondControlPoint.Y + endPoint.Y) / 2);

        // Calculate midpoints of the new line segments
        Point mid12 = new Point((mid1.X + mid2.X) / 2, (mid1.Y + mid2.Y) / 2);
        Point mid23 = new Point((mid2.X + mid3.X) / 2, (mid2.Y + mid3.Y) / 2);

        Point mid123 = new Point((mid12.X + mid23.X) / 2, (mid12.Y + mid23.Y) / 2);

        // Calculate the sub-segment length
        double segmentLength = GetLength(startPoint, endPoint);
        Log.Debug(
            "Approximating cubic curve between {Start} and {End} with length {Length}",
            startPoint,
            endPoint,
            segmentLength);

        foreach (Point point in ApproximateCubicBezier(startPoint, mid1, mid12, mid123)
                     .Concat(ApproximateCubicBezier(mid123, mid23, mid3, endPoint)))
        {
            yield return point;
        }
    }

    private static LinearRing CreateLinearRing(this Coordinate[] coordinates)
    {
        return new LinearRing(
            new LineString(coordinates).IsClosed ? coordinates : coordinates.Append(coordinates[0].Copy()).ToArray());
    }

    private static double GetLength(Point startPoint, Point endPoint)
    {
        return Math.Sqrt(Math.Pow(endPoint.X - startPoint.X, 2) + Math.Pow(endPoint.Y - startPoint.Y, 2));
    }

    private static bool IsSufficientlyFlat(
        Point startPoint,
        Point firstControlPoint,
        Point secondControlPoint,
        Point endPoint)
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

    private static Point Reflect(Point point, Point mirror)
    {
        double num1 = Math.Abs(mirror.X - point.X);
        double num2 = Math.Abs(mirror.Y - point.Y);
        return new Point(
            mirror.X + (mirror.X >= point.X ? num1 : -num1),
            mirror.Y + (mirror.Y >= point.Y ? num2 : -num2));
    }

    private static PointF ToAbsolute(this PointF point, bool isRelative, PointF start)
    {
        if (float.IsNaN(point.X))
        {
            point.X = start.X;
        }
        else if (isRelative)
        {
            point.X += start.X;
        }

        if (float.IsNaN(point.Y))
        {
            point.Y = start.Y;
        }
        else if (isRelative)
        {
            point.Y += start.Y;
        }

        return point;
    }

    private static LinearRing ToLinearRing(this SvgPolygon polygon)
    {
        return CreateLinearRing(
            EnumerableExtensions.Chunk(polygon.Points, 2)
                .Select(points => points.ToArray())
                .Select(points => new Coordinate(points[0], points[1]))
                .ToArray());
    }

    private static LinearRing ToLinearRing(this SvgRectangle rectangle)
    {
        return CreateLinearRing(
            new[]
                {
                    new Coordinate(rectangle.X.Value, rectangle.Y.Value),
                    new Coordinate(rectangle.X.Value + rectangle.Width, rectangle.Y.Value),
                    new Coordinate(rectangle.X.Value + rectangle.Width, rectangle.Y.Value + rectangle.Height),
                    new Coordinate(rectangle.X.Value, rectangle.Y.Value + rectangle.Height),
                });
    }

    private static IEnumerable<LinearRing> ToLinearRings(this SvgPath path)
    {
        return path.PathData.ToAbsolute()
            .ChunkBy(pathSegment => pathSegment is SvgMoveToSegment)
            .Select(ApproximateClosedPath)
            .Select(points => points.Select(point => new Coordinate(point.X, point.Y)).ToArray())
            .Where(coordinates => coordinates.Length >= 2)
            .Select(CreateLinearRing);
    }
}