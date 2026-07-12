import "htmx.org";
import Alpine from "alpinejs";

import "./logs";
import "./sidebar";
import { initBoardPage, initBoardViewPersistence } from "./board";
import { initCommentThreads } from "./commentThread";
import { initConfigPage } from "./config";
import { initConfirmForms } from "./confirm";
import { initJsonHighlight } from "./json-highlight";
import { initMethodologyPreview } from "./methodology-preview";
import { initNodeEdit } from "./nodeEdit";
import { initWorkflowViz } from "./workflow-viz";
import { initWorkspacePersistence } from "./workspace";

Alpine.start();
initConfigPage();
initWorkspacePersistence();
initBoardViewPersistence();
initBoardPage();
initNodeEdit();
initCommentThreads();
initWorkflowViz();
initMethodologyPreview();
initJsonHighlight();
initConfirmForms();
