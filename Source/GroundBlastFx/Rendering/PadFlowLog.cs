namespace GroundBlastFx.Rendering
{
    [System.Flags]
    internal enum PadFlowEvent { None = 0, FeedStart = 1, FeedStop = 2, PulseStart = 4, PulseEnd = 8 }

    /// <summary>
    /// Diagnostic des transitions uniquement : un apport stable, un redessin ou une pause ne répète pas le journal.
    /// Aucune influence sur le rendu ni allocation ; le RenderCore borne aussi le nombre de messages par scène.
    /// </summary>
    internal struct PadFlowLog
    {
        private bool _feeding, _pulse;

        public PadFlowEvent Step(bool feeding, bool pulse, bool fresh)
        {
            if (fresh) this = default;
            PadFlowEvent events = PadFlowEvent.None;
            if (feeding != _feeding) events |= feeding ? PadFlowEvent.FeedStart : PadFlowEvent.FeedStop;
            if (pulse != _pulse) events |= pulse ? PadFlowEvent.PulseStart : PadFlowEvent.PulseEnd;
            _feeding = feeding; _pulse = pulse;
            return events;
        }
    }
}
