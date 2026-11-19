namespace SideHub.Agent.Tests;

public class SecretMaskerTests
{
    private static SecretMasker Masker(Dictionary<string, string> env, params string[] secretKeys) =>
        SecretMasker.For(env, secretKeys) ?? throw new InvalidOperationException("expected a masker");

    /// <summary>Feeds the chunks one by one then flushes, as the PTY reader does until the PTY exits.</summary>
    private static string Stream(SecretMasker masker, params string[] chunks) =>
        string.Concat(chunks.Select(masker.Process)) + masker.Flush();

    [Fact]
    public void Secret_value_is_masked()
    {
        var masker = Masker(new() { ["GITHUB_TOKEN"] = "ghp_s3cr3t" }, "GITHUB_TOKEN");

        Assert.Equal("token=*** done\r\n", Stream(masker, "token=ghp_s3cr3t done\r\n"));
    }

    [Fact]
    public void Every_occurrence_is_masked()
    {
        var masker = Masker(new() { ["API_KEY"] = "abcd1234" }, "API_KEY");

        Assert.Equal("*** and ***", Stream(masker, "abcd1234 and abcd1234"));
    }

    [Fact]
    public void Variables_not_marked_secret_are_left_alone()
    {
        var masker = Masker(new() { ["SIDEHUB_RUN_ID"] = "run-visible", ["API_KEY"] = "abcd1234" }, "API_KEY");

        Assert.Equal("run-visible ***", Stream(masker, "run-visible abcd1234"));
    }

    [Fact]
    public void No_masker_without_secret_keys()
    {
        Assert.Null(SecretMasker.For(new Dictionary<string, string> { ["API_KEY"] = "abcd1234" }, null));
        Assert.Null(SecretMasker.For(new Dictionary<string, string> { ["API_KEY"] = "abcd1234" }, []));
        Assert.Null(SecretMasker.For(null, ["API_KEY"]));
    }

    [Fact]
    public void Secret_keys_missing_from_the_environment_are_ignored()
    {
        Assert.Null(SecretMasker.For(new Dictionary<string, string> { ["OTHER"] = "abcd1234" }, ["API_KEY"]));
    }

    [Fact]
    public void Values_too_short_or_empty_are_not_masked()
    {
        Assert.Null(SecretMasker.For(new Dictionary<string, string> { ["A"] = "abc", ["B"] = "" }, ["A", "B"]));
    }

    [Fact]
    public void Value_cut_between_two_chunks_is_masked()
    {
        var masker = Masker(new() { ["API_KEY"] = "abcd1234" }, "API_KEY");

        Assert.Equal("key=", masker.Process("key=abc"));
        Assert.True(masker.HasPending);
        Assert.Equal("*** ok", masker.Process("d1234 ok"));
        Assert.False(masker.HasPending);
    }

    [Fact]
    public void Value_spread_over_many_chunks_is_masked()
    {
        var masker = Masker(new() { ["API_KEY"] = "abcd1234" }, "API_KEY");

        Assert.Equal("[***]", Stream(masker, "[a", "b", "cd", "12", "34]"));
    }

    [Fact]
    public void Held_back_start_that_turns_out_not_to_be_a_secret_is_released()
    {
        var masker = Masker(new() { ["API_KEY"] = "abcd1234" }, "API_KEY");

        Assert.Equal("x", masker.Process("xabc"));
        Assert.Equal("abcX", masker.Process("X"));
        Assert.False(masker.HasPending);
    }

    [Fact]
    public void Flush_releases_a_held_back_start()
    {
        var masker = Masker(new() { ["API_KEY"] = "abcd1234" }, "API_KEY");

        Assert.Equal("$ ", masker.Process("$ ab"));
        Assert.Equal("ab", masker.Flush());
        Assert.False(masker.HasPending);
        Assert.Equal(string.Empty, masker.Flush());
    }

    [Fact]
    public void Output_without_a_possible_secret_start_is_not_delayed()
    {
        var masker = Masker(new() { ["API_KEY"] = "abcd1234" }, "API_KEY");

        Assert.Equal("hello world\r\n", masker.Process("hello world\r\n"));
        Assert.False(masker.HasPending);
    }

    [Fact]
    public void Retried_start_inside_held_back_text_is_still_found()
    {
        // "aab..." : the first "a" is not the secret, the second one starts it.
        var masker = Masker(new() { ["API_KEY"] = "abcd1234" }, "API_KEY");

        Assert.Equal("a***", Stream(masker, "aa", "bcd1234"));
    }

    [Fact]
    public void Longest_secret_wins_when_one_starts_another()
    {
        var masker = Masker(new() { ["SHORT"] = "abcd", ["LONG"] = "abcd5678" }, "SHORT", "LONG");

        Assert.Equal("***|***", Stream(masker, "abcd5678|abc", "d"));
    }

    [Fact]
    public void Several_secrets_are_masked()
    {
        var masker = Masker(new() { ["A"] = "first-secret", ["B"] = "second-secret" }, "A", "B");

        Assert.Equal("*** / ***", Stream(masker, "first-secret / second-secret"));
    }

    [Fact]
    public void Multi_line_value_is_masked_line_by_line_as_the_terminal_prints_it()
    {
        var masker = Masker(new() { ["PEM"] = "-----BEGIN KEY-----\nMIIBOgIBAAJBAK\n-----END KEY-----" }, "PEM");

        Assert.Equal("***\r\n***\r\n***\r\n",
            Stream(masker, "-----BEGIN KEY-----\r\nMIIBOg", "IBAAJBAK\r\n-----END KEY-----\r\n"));
    }

    [Fact]
    public void Masked_chunks_keep_the_replayed_history_clean()
    {
        var masker = Masker(new() { ["API_KEY"] = "abcd1234" }, "API_KEY");
        var buffer = new PtyOutputBuffer(1024);

        foreach (var chunk in new[] { "echo $API_KEY\r\nab", "cd1234\r\n$ " })
            buffer.Write(masker.Process(chunk));
        buffer.Write(masker.Flush());

        Assert.Equal("echo $API_KEY\r\n***\r\n$ ", buffer.GetAll());
        Assert.DoesNotContain("abcd1234", buffer.GetAll());
    }

    [Fact]
    public void A_secret_added_while_the_terminal_runs_is_masked_from_then_on()
    {
        var masker = Masker(new() { ["API_KEY"] = "abcd1234" }, "API_KEY");
        var before = masker.Process("password s3cret-pw typed\r\n");

        Assert.True(masker.Add("s3cret-pw"));
        var after = Stream(masker, "login with s3cret-pw and abcd1234, then s3cr", "et-pw again\r\n");

        // What was printed before stays as it was: the mask starts when the value is told.
        Assert.Equal("password s3cret-pw typed\r\n", before);
        Assert.Equal("login with *** and ***, then *** again\r\n", after);
    }

    [Fact]
    public void A_terminal_without_secrets_can_get_one_later()
    {
        var masker = SecretMasker.Empty();

        Assert.Equal("nothing to hide\r\n", Stream(masker, "nothing to hide\r\n"));
        Assert.True(masker.Add("line one\nline two"));

        Assert.Equal("*** / ***\r\n", Stream(masker, "line one / line two\r\n"));
    }

    [Fact]
    public void A_value_too_short_or_already_masked_is_not_added_and_the_additions_are_bounded()
    {
        var masker = SecretMasker.Empty();

        Assert.False(masker.Add("abc"));
        Assert.False(masker.Add(null));
        Assert.True(masker.Add("abcd1234"));
        Assert.False(masker.Add("abcd1234"));
        for (var i = 1; i < SecretMasker.MaxAddedSecrets; i++)
            Assert.True(masker.Add($"secret-number-{i}"));
        Assert.False(masker.Add("one-too-many"));
        Assert.Equal("one-too-many ***", Stream(masker, "one-too-many secret-number-7"));
    }
}
