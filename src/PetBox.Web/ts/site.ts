import "htmx.org";
import Alpine from "alpinejs";

import "./logs";
import "./sidebar";
import { initBoardPage } from "./board";
import { initConfigPage } from "./config";
import { initConfirmForms } from "./confirm";
import { initJsonHighlight } from "./json-highlight";
import { hydrateMarkdown } from "./markdown";
import { initMethodologyPreview } from "./methodology-preview";
import { initNodeEdit } from "./nodeEdit";
import { initWorkflowViz } from "./workflow-viz";
import { initWorkspacePersistence } from "./workspace";

Alpine.start();
initConfigPage();
initWorkspacePersistence();
initBoardPage();
initNodeEdit();
initWorkflowViz();
initMethodologyPreview();
initJsonHighlight();
initConfirmForms();
hydrateMarkdown();
