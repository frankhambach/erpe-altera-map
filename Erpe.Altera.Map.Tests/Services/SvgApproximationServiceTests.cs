// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Tests.Services;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

using Erpe.Altera.Map.Services;
using Erpe.Testing;

using FluentAssertions;

using NetTopologySuite.Geometries;

using NUnit.Framework;

using Svg;
using Svg.Pathing;

[TestFixture]
[TestOf(typeof(SvgApproximationService))]
public class SvgApproximationServiceTests
{
    [AutoCustomizedData]
    [Test]
    public void Approximate_ShouldReturnCoordinateSequence_WhenPathContainsCubicBezierSegment(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgApproximationService service = new SvgApproximationService(coordinateSequenceFactory);
        SvgPath path = new SvgPath
            {
                PathData =
                    [
                        new SvgMoveToSegment(false, new PointF(0, 0)),
                        new SvgCubicCurveSegment(false, new PointF(1, 1), new PointF(2, 1), new PointF(3, 0)),
                    ],
            };

        IEnumerable<CoordinateSequence> result = service.Approximate(path);

        result.Should()
            .BeEquivalentTo(
                [
                    coordinateSequenceFactory.Create(
                        [
                            new Coordinate(0, 0), new Coordinate(0.375, 0.328125), new Coordinate(0.75, 0.5625),
                            new Coordinate(1.125, 0.703125), new Coordinate(1.5, 0.75), new Coordinate(1.875, 0.703125),
                            new Coordinate(2.25, 0.5625), new Coordinate(2.625, 0.328125), new Coordinate(3, 0),
                        ]),
                ],
                options => options.WithStrictOrdering());
    }

    [AutoCustomizedData]
    [Test]
    public void
        Approximate_ShouldReturnCoordinateSequence_WhenPathContainsCubicBezierSegmentWithReflectedFirstControlPoint(
            CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgApproximationService service = new SvgApproximationService(coordinateSequenceFactory);
        SvgPath path = new SvgPath
            {
                PathData =
                    [
                        new SvgMoveToSegment(false, new PointF(-1, -1)),
                        new SvgLineSegment(false, new PointF(0, 0)),
                        new SvgCubicCurveSegment(false, new PointF(2, 1), new PointF(3, 0)),
                    ],
            };

        IEnumerable<CoordinateSequence> result = service.Approximate(path);

        result.Should()
            .BeEquivalentTo(
                [
                    coordinateSequenceFactory.Create(
                    [
                        new Coordinate(-1, -1), new Coordinate(0, 0), new Coordinate(0.375, 0.328125),
                        new Coordinate(0.75, 0.5625), new Coordinate(1.125, 0.703125), new Coordinate(1.5, 0.75),
                        new Coordinate(1.875, 0.703125), new Coordinate(2.25, 0.5625), new Coordinate(2.625, 0.328125),
                        new Coordinate(3, 0),
                    ]),
                ],
                options => options.WithStrictOrdering());
    }

    [AutoCustomizedData]
    [Test]
    public void
        Approximate_ShouldReturnCoordinateSequence_WhenPathContainsCubicBezierSegmentWithFirstControlPointReflectedFromPreviousSecondControlPoint(
            CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgApproximationService service = new SvgApproximationService(coordinateSequenceFactory);
        SvgPath path = new SvgPath
            {
                PathData =
                    [
                        new SvgMoveToSegment(false, new PointF(-3, 0)),
                        new SvgCubicCurveSegment(false, new PointF(-2, -1), new PointF(-1, -1), new PointF(0, 0)),
                        new SvgCubicCurveSegment(false, new PointF(2, 1), new PointF(3, 0)),
                    ],
            };

        IEnumerable<CoordinateSequence> result = service.Approximate(path);

        result.Should()
            .BeEquivalentTo(
                [
                    coordinateSequenceFactory.Create(
                        [
                            new Coordinate(-3, 0), new Coordinate(-2.625, -0.328125), new Coordinate(-2.25, -0.5625),
                            new Coordinate(-1.875, -0.703125), new Coordinate(-1.5, -0.75),
                            new Coordinate(-1.125, -0.703125), new Coordinate(-0.75, -0.5625),
                            new Coordinate(-0.375, -0.328125), new Coordinate(0, 0), new Coordinate(0.375, 0.328125),
                            new Coordinate(0.75, 0.5625), new Coordinate(1.125, 0.703125), new Coordinate(1.5, 0.75),
                            new Coordinate(1.875, 0.703125), new Coordinate(2.25, 0.5625),
                            new Coordinate(2.625, 0.328125), new Coordinate(3, 0),
                        ]),
                ],
                options => options.WithStrictOrdering());
    }

    [AutoCustomizedData]
    [Test]
    public void Approximate_ShouldReturnCoordinateSequence_WhenPathContainsRelativeSegments(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgApproximationService service = new SvgApproximationService(coordinateSequenceFactory);
        SvgPath path = new SvgPath
            {
                PathData =
                    [
                        new SvgMoveToSegment(false, new PointF(0, 0)),
                        new SvgLineSegment(true, new PointF(1, 0)),
                        new SvgLineSegment(true, new PointF(0, 1)),
                        new SvgClosePathSegment(false),
                    ],
            };

        IEnumerable<CoordinateSequence> result = service.Approximate(path);

        result.Should()
            .BeEquivalentTo(
                [
                    coordinateSequenceFactory.Create(
                        [
                            new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(1, 1), new Coordinate(0, 0),
                        ]),
                ],
                options => options.WithStrictOrdering());
    }

    [AutoCustomizedData]
    [Test]
    public void Approximate_ShouldReturnCoordinateSequence_WhenPathContainsSegmentsWithPartiallySpecifiedCoordinates(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgApproximationService service = new SvgApproximationService(coordinateSequenceFactory);
        SvgPath path = new SvgPath
            {
                PathData =
                    [
                        new SvgMoveToSegment(false, new PointF(0, 0)),
                        new SvgLineSegment(false, new PointF(1, float.NaN)),
                        new SvgLineSegment(false, new PointF(float.NaN, 1)),
                        new SvgClosePathSegment(false),
                    ],
            };

        IEnumerable<CoordinateSequence> result = service.Approximate(path);

        result.Should()
            .BeEquivalentTo(
                [
                    coordinateSequenceFactory.Create(
                        [
                            new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(1, 1), new Coordinate(0, 0),
                        ]),
                ],
                options => options.WithStrictOrdering());
    }

    [AutoCustomizedData]
    [Test]
    public void Approximate_ShouldReturnCoordinateSequence_WhenPolygonIsProvided(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgApproximationService service = new SvgApproximationService(coordinateSequenceFactory);
        SvgPolygon polygon = new SvgPolygon
            {
                Points =
                    [
                        new SvgUnit(0),
                        new SvgUnit(0),
                        new SvgUnit(1),
                        new SvgUnit(0),
                        new SvgUnit(1),
                        new SvgUnit(1),
                        new SvgUnit(0),
                        new SvgUnit(1),
                    ],
            };

        IEnumerable<CoordinateSequence> result = service.Approximate(polygon);

        result.Should()
            .BeEquivalentTo(
                [
                    coordinateSequenceFactory.Create(
                        [
                            new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(1, 1), new Coordinate(0, 1)
                        ])
                ],
                options => options.WithStrictOrdering());
    }

    [AutoCustomizedData]
    [Test]
    public void Approximate_ShouldReturnCoordinateSequence_WhenRectangleIsProvided(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgApproximationService service = new SvgApproximationService(coordinateSequenceFactory);
        SvgRectangle rectangle = new SvgRectangle
            {
                X = new SvgUnit(0),
                Y = new SvgUnit(0),
                Width = new SvgUnit(1),
                Height = new SvgUnit(1),
            };

        IEnumerable<CoordinateSequence> result = service.Approximate(rectangle);

        result.Should()
            .BeEquivalentTo(
                [
                    coordinateSequenceFactory.Create(
                        [
                            new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(1, 1), new Coordinate(0, 1)
                        ])
                ],
                options => options.WithStrictOrdering());
    }

    [AutoCustomizedData]
    [Test]
    public void Approximate_ShouldReturnEmpty_WhenSvgElementIsUnsupported(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgApproximationService service = new SvgApproximationService(coordinateSequenceFactory);
        SvgCircle unsupportedElement = new SvgCircle();

        IEnumerable<CoordinateSequence> result = service.Approximate(unsupportedElement);

        result.Should().BeEmpty();
    }

    [AutoCustomizedData]
    [Test]
    public void GetCoordinates_ShouldReturnPathSegmentEndPoints_WhenPathContainsMixedSegments(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgPath path = new SvgPath
            {
                PathData =
                    [
                        new SvgMoveToSegment(false, new PointF(0, 0)),
                        new SvgLineSegment(false, new PointF(1, 1)),
                        new SvgCubicCurveSegment(false, new PointF(2, 1), new PointF(2, 0), new PointF(1, 0)),
                        new SvgClosePathSegment(false),
                    ],
            };
        SvgApproximationService service = new SvgApproximationService(coordinateSequenceFactory);

        List<Coordinate> result = service.GetCoordinates(path).ToList();

        result.Should()
            .BeEquivalentTo(
                [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(1, 0), new Coordinate(0, 0)],
                options => options.WithStrictOrdering());
    }

    [AutoCustomizedData]
    [Test]
    public void GetCoordinates_ShouldReturnCoordinates_WhenPathHasNegativeCoordinates(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgPath path = new SvgPath
            {
                PathData =
                    [
                        new SvgMoveToSegment(false, new PointF(-1, -1)),
                        new SvgLineSegment(false, new PointF(-2, -2)),
                        new SvgCubicCurveSegment(false, new PointF(-3, -2), new PointF(-3, -1), new PointF(-2, -1)),
                    ],
            };
        SvgApproximationService service = new SvgApproximationService(coordinateSequenceFactory);

        List<Coordinate> result = service.GetCoordinates(path).ToList();

        result.Should()
            .BeEquivalentTo(
                [new Coordinate(-1, -1), new Coordinate(-2, -2), new Coordinate(-2, -1)],
                options => options.WithStrictOrdering());
    }

    [AutoCustomizedData]
    [Test]
    public void GetCoordinates_ShouldReturnEmpty_WhenPathIsEmpty(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgPath path = new SvgPath
            {
                PathData = [],
            };
        SvgApproximationService service = new SvgApproximationService(coordinateSequenceFactory);

        IEnumerable<Coordinate> result = service.GetCoordinates(path);

        result.Should().BeEmpty();
    }

    [AutoCustomizedData]
    [Test]
    public void GetCoordinates_ShouldThrowArgumentNullException_WhenPathIsNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgApproximationService service = new SvgApproximationService(coordinateSequenceFactory);

        Action action = () => service.GetCoordinates(null!);

        action.Should().Throw<ArgumentNullException>();
    }

    #pragma warning disable SA1204
    public static double GregorianToJulianDay(int year, int month, int day)
    {
        if (month <= 2)
        {
            year -= 1;
            month += 12;
        }

        int a = year / 100;
        int b = 2 - a + (a / 4);

        double julianDay = Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + day + b - 1524;

        return julianDay;
    }

    public static bool IsLeapYear(int year)
    {
        if (year % 400 == 0)
        {
            return true;
        }

        if (year % 100 == 0)
        {
            return false;
        }

        if (year % 4 == 0)
        {
            return true;
        }

        return false;
    }

    public static (int Year, int Month, int Day, string? SpecialDay) JulianDayToPositivist(double julianDay)
    {
        int days = (int)Math.Floor(julianDay);

        int year = 0;

        // Handle 400-year blocks
        int quadricenturies = days / 146097;
        year += quadricenturies * 400;
        days %= 146097;

        // Handle 100-year blocks
        int centuries = days / 36524;
        if (centuries == 4)
        {
            centuries = 3; // avoid overshooting
        }

        year += centuries * 100;
        days -= centuries * 36524;

        // Handle 4-year blocks
        int quadrennia = days / 1461;
        year += quadrennia * 4;
        days %= 1461;

        // Handle single years
        int yearsInCycle = days / 365;
        if (yearsInCycle == 4)
        {
            yearsInCycle = 3; // avoid overshooting
        }

        year += yearsInCycle;
        days -= yearsInCycle * 365;

        int dayOfYear = days;

        bool leap = IsLeapYear(year);

        ++year;
        if ((!leap && dayOfYear == 364) || (leap && dayOfYear == 365))
        {
            string specialDay = (!leap) ? "Year Day" : "Leap Day";
            return (year, 0, 0, specialDay);
        }
        else
        {
            int month = (dayOfYear / 28) + 1;
            int day = (dayOfYear % 28) + 1;
            return (year, month, day, null);
        }
    }

    [Test]
    public void TestJDConversion()
    {
        int year = 2030;
        int month = 5;
        int day = 10;

        double julianDay = GregorianToJulianDay(year, month, day);
        Console.WriteLine($"Julian Day for {year}-{month}-{day} is: {julianDay}");

        var positivistDate = JulianDayToPositivist(julianDay);

        if (positivistDate.SpecialDay == null)
        {
            Console.WriteLine(
                $"Positivist Date: Year {positivistDate.Year}, Month {positivistDate.Month}, Day {positivistDate.Day}");
            Console.WriteLine(
                $"Positivist Date: Year {positivistDate.Year:b16}, Month {positivistDate.Month:b4}, Day {positivistDate.Day:b8}");
        }
        else
        {
            Console.WriteLine($"Positivist Special Day in {positivistDate.Year}: {positivistDate.SpecialDay}");
        }

        true.Should().BeTrue();
    }
}