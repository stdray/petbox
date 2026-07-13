// Order matters and is load-bearing: htmx-global imports htmx and publishes window.htmx, which the
// SSE extension (a plain script that registers itself against the global) needs at evaluation time.
// The extension is what makes hx-ext="sse" / sse-connect / sse-swap live — without this import those
// attributes are inert and the log live-tail never opens an EventSource at all
// (live-tail-sse-transport-broken).
//
// The extension comes from the standalone `htmx-ext-sse` package, NOT from htmx.org/dist/ext/sse.js:
// the copy shipped inside htmx.org 2.x is the htmx-1 extension and greets every page load with
// "WARNING: You are using an htmx 1 extension with htmx 2.0.4". Both work; only one does so without
// telling the user its own wiring is wrong.
import "./htmx-global";
import "htmx-ext-sse";
import Alpine from "alpinejs";

import "./logs";
import "./sidebar";
import { initBoardFieldsDialog, initBoardPage } from "./board";
import { initCommentThreads } from "./commentThread";
import { initConfigPage } from "./config";
import { initConfirmForms } from "./confirm";
import { initJsonHighlight } from "./json-highlight";
import { initMethodologyPreview } from "./methodology-preview";
import { initNodeEdit } from "./nodeEdit";
import { initWorkflowViz } from "./workflow-viz";

Alpine.start();
initConfigPage();
initBoardFieldsDialog();
initBoardPage();
initNodeEdit();
initCommentThreads();
initWorkflowViz();
initMethodologyPreview();
initJsonHighlight();
initConfirmForms();
