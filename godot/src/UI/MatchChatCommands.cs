#nullable enable
using ProjectChimera.Dsl; // EventBounds

namespace ProjectChimera.UI
{
    /// <summary>
    /// DW-374 — the presentation-side chat-string→code map the Story 7.13 (Arm D) <c>player_chat</c> rail was built
    /// for, extracted Godot-free (the <see cref="MatchChatFormat"/> precedent) so the mapping is Tier-1 testable
    /// while <c>MatchChatOverlay</c> keeps only the LineEdit plumbing.
    ///
    /// <para>The sim tick only ever sees a bounded integer chat CODE + the sender's faction slot — NEVER a string
    /// (<see cref="EventBounds.MaxChatCode"/>). The authorable contract is the WC3-style dash-command: a typed
    /// message that is exactly <c>-&lt;ASCII digits&gt;</c> (after trimming surrounding whitespace) maps to that
    /// integer code, and <c>MatchChatOverlay</c> raises it via <c>LockstepManager.SendPlayerChat</c> — so an
    /// authored trigger subscribed to <c>player_chat</c> with <c>event.code == N</c> fires when any player types
    /// <c>-N</c>. Every other message is free text: display-only on the reliable chat side-channel, never
    /// sim-visible.</para>
    ///
    /// <para>The parse is ordinal and culture-proof (ASCII digits only, hand-accumulated — no <c>int.Parse</c>),
    /// never throws, and never yields an out-of-range code: the same typed string maps to the same code on every
    /// machine and build, which is what makes a hardcoded <c>event.code</c> comparison in authored scenario JSON a
    /// stable contract. Anything else — free text, a bare "-", embedded text, non-ASCII digits, a value ≥
    /// <see cref="EventBounds.MaxChatCode"/> — is simply not a command.</para>
    /// </summary>
    public static class MatchChatCommands
    {
        /// <summary>
        /// Try to parse a typed chat message as a dash-command chat code: trimmed, exactly <c>-&lt;ASCII digits&gt;</c>,
        /// value in <c>[0, EventBounds.MaxChatCode)</c>. Returns false (and a zeroed <paramref name="chatCode"/>)
        /// for anything else — null/blank input, free text, a bare dash, non-digit tails, non-ASCII digits, or an
        /// out-of-range value. Never throws; accumulation is capped below the bound so it cannot overflow.
        /// </summary>
        public static bool TryParseChatCode(string? message, out int chatCode)
        {
            chatCode = 0;
            if (message == null) return false;

            // Allocation-free trim: locate the first/last non-whitespace characters.
            int start = 0, end = message.Length - 1;
            while (start <= end && char.IsWhiteSpace(message[start])) start++;
            while (end >= start && char.IsWhiteSpace(message[end]))   end--;

            // Shape: '-' followed by at least one digit and nothing else.
            if (end - start < 1 || message[start] != '-') return false;

            int value = 0;
            for (int i = start + 1; i <= end; i++)
            {
                char c = message[i];
                if (c < '0' || c > '9') return false;               // ordinal ASCII only — '٤'/'４' is free text, not a command
                value = value * 10 + (c - '0');
                if (value >= EventBounds.MaxChatCode) return false; // out of range; also caps accumulation (no overflow)
            }

            chatCode = value;
            return true;
        }
    }
}
