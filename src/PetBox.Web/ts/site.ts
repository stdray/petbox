import "htmx.org";
import Alpine from "alpinejs";

import "./logs";
import "./sidebar";
import { initBoardPage } from "./board";
import { initConfigPage } from "./config";
import { hydrateMarkdown } from "./markdown";
import { initNodeEdit } from "./nodeEdit";
import { initWorkflowViz } from "./workflow-viz";
import { initWorkspacePersistence } from "./workspace";

Alpine.start();
initConfigPage();
initWorkspacePersistence();
initBoardPage();
initNodeEdit();
initWorkflowViz();
hydrateMarkdown();
