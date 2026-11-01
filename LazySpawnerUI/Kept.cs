using System;
using System.Collections.Generic;
using UnityEngine;

namespace LazySpawner;

// A value that's asked for every frame but rarely changes: made again only when its key changes, or once it's
// older than its lifetime.
internal class Kept<TKey, T>
{
    private readonly Func<TKey, T> make;
    private readonly float lifetime;
    private (bool made, TKey key, T value, float until) kept;

    public Kept(Func<TKey, T> make, float lifetime = float.PositiveInfinity)
    {
        this.make = make;
        this.lifetime = lifetime;
    }

    public T Get(TKey key = default)
    {
        if (!kept.made || Time.unscaledTime >= kept.until || !EqualityComparer<TKey>.Default.Equals(key, kept.key))
            kept = (true, key, make(key), Time.unscaledTime + lifetime);
        return kept.value;
    }

    public void Forget() => kept = default;
}
