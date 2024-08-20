// <copyright file="Country.cs" company="Frank Hambach">
// Copyright (c) Frank Hambach. All rights reserved.
// </copyright>

namespace Erpe.Altera.Map.Models;

using System.Collections.Generic;

public sealed class Country(string id)
{
    public Country(string id, IEnumerable<Shape> shapes)
        : this(id)
    {
        foreach (Shape shape in shapes)
        {
            this.Shapes.Add(shape);
        }
    }

    public string Id { get; } = id;

    public IList<Shape> Shapes { get; } = new List<Shape>();
}