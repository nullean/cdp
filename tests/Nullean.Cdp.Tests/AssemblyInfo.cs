// A browser test that loses a reply would otherwise wait forever and hang the run (found by mutating reply routing).
[assembly: Timeout(60_000)]
