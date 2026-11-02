# Glossary

Terms are listed alphabetically. *Italic* words inside a definition are defined elsewhere in
this glossary. Watchpoint deliberately uses its own vocabulary; where a common industry term
exists it is given in brackets.

| Term | Definition |
|---|---|
| **Acknowledge** | Operator action stating "I have seen this *alarm* and I am handling it". Recorded with user, time and workstation. |
| **Active alarm** | An *alarm* that is not yet closed. |
| **Alarm** | A situation requiring operator attention, created by an *alarm rule* from an *event* or a *device* state. Has a lifecycle (raised → acknowledged → resolved → closed). |
| **Alarm procedure** | Ordered steps (instructions, questions, checklists, mandatory comment) an operator follows while handling an alarm. [Operator instructions, SOP] |
| **Alarm rule** | Configuration saying which events or states raise an alarm, with which priority, delays, procedure, actions and escalation. Separate from the device. |
| **Area** (intrusion) | A part of an intrusion system that is armed and disarmed as a whole. Modelled as a *device* with the *Armable* *capability*. Not to be confused with *location*. [Partition] |
| **Audit trail** | Append-only journal of every user and system action that changes something: sign-in, command, configuration change, alarm handling. |
| **Automation** | A no-code flow made of a *trigger*, optional conditions and actions, edited visually. [Scenario] |
| **Capability** | A reusable, versioned definition of behaviour: a set of typed *properties*, *commands* and *events*. Examples: *Openable*, *Armable*, *VideoSource*, *PanTiltZoom*, *Measurement*, *Health*. |
| **Change set** | A group of configuration changes made in a *draft* and published together. |
| **Cluster** | The set of *nodes* serving one *site*, sharing one database and one event log. |
| **Command** | A typed request sent to a *device* (open, arm, move to preset…). Has an id, an issuer, a priority, a timeout and a result. |
| **Command result** | The outcome of a command: succeeded, failed (with reason), rejected, timed out, cancelled; optionally *verified* when the expected state was observed. |
| **Configuration mode** | The part of the desktop client that edits configuration. Visible only to users with configuration rights. |
| **Connection** | A configured, running instance of a *connector*, bound to one *server*. |
| **Connector** | The class in an *integration package* that implements the integration contract for one kind of *server*. |
| **Core** | The part of the *node* that owns devices, events, alarms, commands, history and automation. Knows nothing about vendor protocols. |
| **Desktop client** | The Watchpoint application installed on Windows, Linux or macOS. Does operation and configuration. |
| **Device** | Anything a *server* hosts that Watchpoint can observe or command. Has a *device type*, a *location*, *properties*, and can contain sub-devices. |
| **Device type** | A named combination of *capabilities* plus metadata (category, icon, symbol, licence class, default alarm rules). |
| **Draft** | A private working copy of configuration changes not yet applied to the live system. |
| **Event** | Something that happened at a given time: a door forced, a badge refused, a zone in alarm, a user signing in. Immutable once recorded. |
| **Event log** | The durable, ordered stream of everything that happens on a site (in NATS JetStream), from which live state and history are built. |
| **Federation** | Several *sites* linked so that a central site can supervise the others. |
| **Fencing token** | A number that increases each time leadership changes. Writes carrying an old token are refused, which prevents two nodes acting as leader at once. |
| **Footprint** | A set of identifiers of a machine, used to bind a *licence* to it. |
| **Health** | The standard *capability* every device has: connectivity (online, offline, degraded, unknown) and a list of faults (tamper, battery low…). |
| **Integration** | Support for a family of external systems (e.g. "Salto Space"), delivered as an *integration package*. |
| **Integration host** | A process that runs *connections*. Either the *node* itself (in-process) or an isolated worker process managed by the node, possibly on another machine. |
| **Integration package** | A signed archive/folder containing the connector DLLs, the vendor SDK DLLs, a manifest, translations, symbols and default rules. |
| **Leader** | In a *cluster*, the node currently responsible for a given task (for example a given connection). Holds a lease with a *fencing token*. |
| **Licence** | A signed file that states what a site may use (edition, device units, client seats, options) and on which machines. |
| **Licence unit** | The counting unit for device licensing. Each *device type* has a licence class: full (1 unit), light (0.1 unit) or free. |
| **Location** | A node of the physical tree: site → building → floor → room/zone. Used for navigation, filtering and rights. |
| **Maintenance mode (system)** | A state where operation is suspended for operator users (desktop, web, mobile) while configurators keep working. Event processing continues. |
| **Maintenance mode (device)** | A device flag that suppresses its alarms during works. |
| **Media gateway** | The component that receives video from cameras and VMS (RTSP or SDK) and serves it to clients as WebRTC (or HLS fallback). |
| **Node** | One running Watchpoint service on one machine. |
| **Operation mode** | The part of the clients used to supervise: alarms, events, synoptics, video, commands. |
| **Priority** | Importance of an alarm, 1 (highest) to 1000, displayed through named bands (Critical, High, Medium, Low, Info) configurable per site. |
| **Property** | A typed value of a device defined by a capability (door position, armed state, temperature). Each value has a quality and timestamps. |
| **Quality** | Trust level of a property value: good, uncertain, bad, stale. |
| **Rights** | What a user may do (permissions), on what (scopes), and when (schedule). Stored per user. |
| **Rights copy** | The tool that copies all rights of one user (or a template user) to other users or to all members of a user group. |
| **Script** | TypeScript code reacting to events or schedules, executed in the sandbox with explicitly granted rights. |
| **Script identity** | The non-human account a script runs as. Has its own rights like a user. |
| **Server** | Any external system hosting devices: access control server, VMS, intrusion panel, fire panel, BMS controller, IoT broker. Can contain sub-servers. |
| **Site** | One supervised installation with its own *cluster*. |
| **Synoptic** | An interactive screen showing devices on a plan, a map or a diagram, with live states and commands. |
| **Symbol** | The visual representation of a device type on a synoptic, with one appearance per state. |
| **Tag** | A free label attached to devices, servers or locations, used for grouping, filtering, rights and automations. |
| **Template user** | A user flagged as a template: cannot sign in, used only as a source for *rights copy*. |
| **Trigger** | What starts an automation or script: an event, a state change, an alarm change, a schedule, a manual start. |
| **User group** | A named set of users, used to organise them and to apply rights in bulk. A group does not hold rights itself. |
| **Verified (command)** | A command whose expected effect was observed on the device state within the verification window. |
| **Workspace** | A saved arrangement of client windows, panels and screens across monitors. |
