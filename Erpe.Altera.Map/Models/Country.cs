// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

using System;
using System.Collections.Generic;

public sealed class Country(string id, CountryType type)
{
    public Country(string id, CountryType type, IEnumerable<Shape> shapes)
        : this(id, type)
    {
        ArgumentNullException.ThrowIfNull(shapes);

        foreach (Shape shape in shapes)
        {
            this.Shapes.Add(shape);
        }
    }

    public string Id { get; } = id;

    public CountryType Type { get; set; } = type;

    public IList<Shape> Shapes { get; } = new List<Shape>();
}