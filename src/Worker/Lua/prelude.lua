-- Zenkins Lua prelude (card F-014, docs/lua-runtime.md §6).
--
-- Shipped as an embedded resource of the worker binary and loaded as its own chunk before
-- pipeline.lua: it defines the declarative helpers (pipeline, step, log, is_windows/is_linux/
-- is_macos) as ordinary Lua functions, so the definition phase needs no host access beyond the
-- log sink and has nothing to do with I/O.
--
-- Lua 5.2 only (docs/lua-runtime.md §4): no //, no bitwise operators, no utf8, no <const>.
--
-- The host reads the pipeline table back from the global below when the script has no `return`;
-- a script that writes `return pipeline { ... }` works just as well (a returned value wins).

_zenkins_pipeline = nil

local function require_table(value, what, level)
  if type(value) ~= "table" then
    error(what .. " takes a table, got " .. type(value), (level or 1) + 1)
  end
  return value
end

-- pipeline{ name = ..., steps = { ... } }
--
-- The declarative form of PRD §3.3.2. It only records the table: validation is the host's job, so
-- a spec is rejected with one field path (`pipeline.steps[2].run`) instead of a vague Lua error, and
-- the frozen JSON is produced by the host from this very table (docs/lua-runtime.md §6.1).
--
-- F-030: a pipeline may group its steps into stages instead of listing them flat:
--
--   pipeline { name = "release", stages = {
--     { name = "Build", steps = { step{ name = "compile", run = {...} } } },
--     { name = "Test",  steps = { ... } },
--     { steps = { step{ name = "notify", run = {...} } } },   -- an entry with no name: unstaged
--   } }
--
-- The name is written onto every step the entry contains (the host flattens the entries back into
-- one ordered step list), so the frozen spec - not a reader's guess - is what a run page groups by.
-- `stages` is an ordered list, not a map: a Lua map has no declaration order to show.
--
-- F-031: post steps run after the main list.
--
--   pipeline { name = "release", steps = { ... }, post = {
--     success = { step{ name = "report",  run = {...} } },   -- only when the main list succeeded
--     failure = { step{ name = "notify",  run = {...} } },   -- only when it failed (never both)
--     always  = { step{ name = "cleanup", run = {...} } },   -- last, whatever happened
--   } }
--
-- The host freezes each post step's name with the `post/` prefix (declare the plain name), runs the
-- matching set and then `always` through the same C# step executor, and gives `always` a bounded grace
-- window after a cancel. A post step's failure is shown as its own step and does not change the run's
-- result unless the project says so.
function pipeline(spec)
  require_table(spec, "pipeline{}", 1)
  _zenkins_pipeline = spec
  return spec
end

-- step{ name = ..., run = { ... }, stage = "Build" }
--
-- Accepted for readability and for the imperative form F-015 will bring; today it is the identity
-- function, so `steps = { step { ... }, step { ... } }` and plain tables are the same thing - and
-- so is `stage = "Build"` (F-030): it is recorded onto the step, read back by the host and validated
-- there (non-empty, at most 64 characters, no surrounding whitespace).
function step(spec)
  return require_table(spec, "step{}", 1)
end

-- log("line") - or log("a", 1, true), rendered like Lua's print.
--
-- `print` is re-bound by the host to the run's log sink (docs/lua-runtime.md §7): a line a pipeline
-- emits while being described still shows up in the run log, in the run's own stream, redacted the
-- same way every other line is.
function log(...)
  print(...)
end

local function platform_is(name)
  return _zenkins_platform == name
end

function is_windows()
  return platform_is("windows")
end

function is_linux()
  return platform_is("linux")
end

function is_macos()
  return platform_is("macos")
end
