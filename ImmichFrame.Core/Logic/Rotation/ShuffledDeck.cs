using System;
using System.Collections.Generic;
using System.Linq;

namespace ImmichFrame.Core.Logic.Rotation
{
    internal sealed class ShuffledDeck<T>
    {
        private readonly Func<IEnumerable<T>> _source;
        private readonly Random _rng = new();
        private readonly object _sync = new();
        private List<T> _deck = new();

        public ShuffledDeck(Func<IEnumerable<T>> source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            RefillLocked();
        }

        public T Next(Func<T, bool>? exclude = null)
        {
            exclude ??= _ => false;

            lock (_sync)
            {
                while (true)
                {
                    if (_deck.Count == 0)
                    {
                        RefillLocked();
                        if (_deck.Count == 0)
                            throw new InvalidOperationException("No candidates available for exhaustive shuffle.");
                    }

                    var item = _deck[^1];
                    _deck.RemoveAt(_deck.Count - 1);

                    if (!exclude(item))
                        return item;
                    // sonst: Schleife erneut, evtl. nächstes Element ziehen
                }
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _deck.Clear();
            }
        }

        // Achtung: wird nur unter _sync aufgerufen!
        private void RefillLocked()
        {
            _deck = _source().ToList();

            if (_deck.Count == 0)
                return;

            // Fisher–Yates
            for (int i = _deck.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (_deck[i], _deck[j]) = (_deck[j], _deck[i]);
            }
        }
    }
}
