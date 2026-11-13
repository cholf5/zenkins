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
function pipeline(spec)
  require_table(spec, "pipeline{}", 1)
  _zenkins_pipeline = spec
  return spec
end

-- step{ name = ..., run = { ... } }
--
-- Accepted for readability and for the imperative form F-015 will bring; today it is the identity
-- function, so `steps = { step { ... }, step { ... } }` and plain tables are the same thing.
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
