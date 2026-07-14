using System;

namespace FixtureLib;

/// <summary>extract_interface fixture: a generic class whose type constraint must round-trip onto the generated interface.</summary>
public class Box<T> where T : class, IComparable<T>
{
    public T? Value { get; set; }

    public T? Get() => Value;
}
