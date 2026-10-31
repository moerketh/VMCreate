using VMCreate.Metadata;

namespace VMCreate.Manager.Tests.Metadata
{
    [TestClass]
    public class NotesBlockTests
    {
        [TestMethod]
        public void Parse_NoBlock_ReturnsNotesAsFreeText()
        {
            var (free, payload) = NotesBlock.Parse("just my notes");
            Assert.AreEqual("just my notes", free);
            Assert.IsNull(payload);
        }

        [TestMethod]
        public void Parse_NullOrEmpty_ReturnsEmpty()
        {
            Assert.AreEqual("", NotesBlock.Parse(null).FreeText);
            Assert.AreEqual("", NotesBlock.Parse("").FreeText);
        }

        [TestMethod]
        public void ComposeThenParse_RoundTripsPayloadAndFreeText()
        {
            var payload = new NotesPayload
            {
                Folder = "Engagements/ACME",
                Tags = new List<string> { "kali", "external" },
                Purpose = "Jump box",
                Protected = true,
                ExpiresOn = new DateOnly(2026, 12, 31),
            };

            string notes = NotesBlock.Compose("my own text", payload);
            var (free, parsed) = NotesBlock.Parse(notes);

            Assert.AreEqual("my own text", free);
            Assert.IsNotNull(parsed);
            Assert.AreEqual("Engagements/ACME", parsed.Folder);
            CollectionAssert.AreEqual(new[] { "kali", "external" }, parsed.Tags);
            Assert.AreEqual(true, parsed.Protected);
            Assert.AreEqual(new DateOnly(2026, 12, 31), parsed.ExpiresOn);
        }

        [TestMethod]
        public void Compose_PutsUserTextFirst()
        {
            string notes = NotesBlock.Compose("hello", new NotesPayload { Purpose = "x" });
            StringAssert.StartsWith(notes, "hello\r\n[vmcreate]");
            StringAssert.EndsWith(notes, "[/vmcreate]");
        }

        [TestMethod]
        public void Compose_NullPayload_RemovesBlock()
        {
            Assert.AreEqual("hello", NotesBlock.Compose("hello\r\n", null));
        }

        [TestMethod]
        public void Update_ReplacesExistingBlockAndKeepsText()
        {
            string original = NotesBlock.Compose("keep me", new NotesPayload { Purpose = "old" });
            string updated = NotesBlock.Update(original, new NotesPayload { Purpose = "new" });

            var (free, parsed) = NotesBlock.Parse(updated);
            Assert.AreEqual("keep me", free);
            Assert.AreEqual("new", parsed!.Purpose);
            Assert.AreEqual(1, CountOccurrences(updated, NotesBlock.StartMarker));
        }

        [TestMethod]
        public void Update_IsIdempotent()
        {
            var payload = new NotesPayload { Tags = new List<string> { "a" } };
            string once = NotesBlock.Update("text", payload);
            Assert.AreEqual(once, NotesBlock.Update(once, payload));
        }

        [TestMethod]
        public void Parse_MalformedBlock_StaysInFreeText()
        {
            string notes = "text\r\n[vmcreate]{not json[/vmcreate]";
            var (free, payload) = NotesBlock.Parse(notes);
            Assert.IsNull(payload);
            Assert.AreEqual(notes, free);
        }

        [TestMethod]
        public void Update_WithMalformedBlock_AddsValidBlockThatParses()
        {
            string notes = "text\r\n[vmcreate]{not json[/vmcreate]";
            string updated = NotesBlock.Update(notes, new NotesPayload { Purpose = "p" });
            Assert.AreEqual("p", NotesBlock.Parse(updated).Payload!.Purpose);
        }

        [TestMethod]
        public void Parse_BlockInMiddle_RemovesOnlyBlock()
        {
            string notes = "line1\r\n[vmcreate]{\"purpose\":\"p\"}[/vmcreate]\r\nline2";
            var (free, payload) = NotesBlock.Parse(notes);
            Assert.AreEqual("p", payload!.Purpose);
            Assert.AreEqual("line1line2", free.Replace("\r\n", ""));
        }

        private static int CountOccurrences(string s, string value)
        {
            int count = 0;
            for (int i = s.IndexOf(value, StringComparison.Ordinal); i >= 0; i = s.IndexOf(value, i + 1, StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
