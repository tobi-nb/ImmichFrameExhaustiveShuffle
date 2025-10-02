using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ImmichFrame.Core.Logic.Rotation
{
    internal sealed class ExhaustiveRotationStrategy<T>
    {
        private readonly Func<IEnumerable<T>> _candidatesProvider;
        private readonly ConcurrentDictionary<string, ShuffledDeck<T>> _decks = new();

        public ExhaustiveRotationStrategy(Func<IEnumerable<T>> candidatesProvider)
        {
            _candidatesProvider = candidatesProvider ?? throw new ArgumentNullException(nameof(candidatesProvider));
        }

        private ShuffledDeck<T> GetDeck(string key)
            => _decks.GetOrAdd(key, _ => new ShuffledDeck<T>(_candidatesProvider));

        public T Next(string key, Func<T, bool>? exclude = null)
            => GetDeck(key).Next(exclude);

        public void Reset(string key)
        {
            if (_decks.TryGetValue(key, out var deck))
                deck.Reset();
        }
    }
}
