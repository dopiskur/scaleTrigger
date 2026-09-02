// Entry point loaded by index.html as a module script. Each import below wires up its own
// event listeners and kicks off its own initial data load as a side effect of module
// evaluation (report polling, the first LoadConfig fetch, hardware info) - this file's only
// job is to pull the rest of the dependency graph in, in one place.
import './report.js';
import './loadconfig.js';
import './benchmark.js';
