SHORT DESCRIPTION
---------------------------
Greenhouse Island: a floating platform off the Docks with a two-storey glass dome, bought from Ray's Realty for $0. Build on both floors, hire up to 30 employees, and recolour the giant weed plant and the turbine lights.


FULL DESCRIPTION (same layout as the page: headings in [size=4][b]...[/b][/size])
----------------

[size=4][b]Description[/b][/size]
Greenhouse Island adds a brand-new property to Schedule I: a round timber platform floating in the sea just off the Docks, with a two-storey glass geodesic dome in the middle. Walk out to it along a walkway from the quay, buy it from Ray's Realty for $0, and use it like any other property: build grow setups inside the dome, hire employees and save the game at the intercom by the door.

[size=4][b]Installation instructions[/b][/size]
1. Install MelonLoader 0.7.x into Schedule I and start the game once so it sets itself up.
2. Put "Greenhouse Island.dll" into the game's Mods folder.
3. Start the game and load a save. The island is built near the Docks automatically.
4. Walk down the walkway from the Docks quay, then buy it from Ray's Realty for $0 (talk to Ray, choose to buy a property, pick "Greenhouse Island").

[size=4][b]Main features[/b][/size]
The island:
- A floating platform (36 m across): a plank deck on a timber hull ringed with white pontoons, with lanterns and a ring of the game's own trees round the edge.
- Three walkways: two from the Docks quay and one from Manny's warehouse. Stairs take you down from the edge of the land (even a tall quay), then a railed walkway runs onto the deck. Where the quay has a railing, the walkway steps up and over it.
- A glass geodesic dome (24 m across) with a two-storey layout: build tiles cover the whole ground floor and a glass upper floor, reached by wooden stairs along the dome wall.
- Sliding glass doors facing each walkway, locked until you own the island, then they open by themselves for you and your employees.
- A save point just inside the door, like the intercom at other properties. It works once the property is yours.
- A real property: it is listed at Ray's Realty for $0, and everything you place in the dome is saved with it.

Make it yours:
- A giant weed plant grows in the middle of the dome. Press E on its trunk to change the colour of its leaves and of its buds.
- A wind turbine faces the Docks, with water tanks beside it. Its lights stay on day and night; press E on the mast to change their colour.
- A water tap on the dome wall for filling watering cans, just like the taps at other properties.

Running it:
- Room for up to 30 employees, who wait on the deck ring behind the dome until you give them a bed or locker. (Set EmployeeCapacity from 0 to 30 if you want fewer.)
- Three loading bays that work in the Deliveries app: bays 1 and 2 on the Docks road, bay 3 on the deck.
- A dumpster and a trash can spot for your cleaners.
- Shows up as "Greenhouse" in the Wages Manager app if you have that mod.

[size=4][b]Settings[/b][/size]
In UserData/MelonPreferences.cfg, under [PelicanKey] (created the first time the game loads the mod):
- Enabled (true): set it to false to switch the island off.
- EmployeeCapacity (30): how many employees the island can hold, 0 to 30.
- TreeColour, BudColour, WindmillLightColour: hex colours, normally set in game by pressing E on the plant or the turbine.

[size=4][b]Requirements[/b][/size]
- Schedule I, IL2CPP version (the normal Steam branch): works on 0.4.7 (the current stable) and on 0.4.6f13. Not for the Mono ("alternate") branches.
- MelonLoader 0.7.x.
- Single player, or you are the host in multiplayer (the island property is created by the host).
- Building is possible inside the dome only (both floors). The outside deck can't be built on, except for the small patch where the cleaners' trash can goes.

[size=4][b]Troubleshooting[/b][/size]
- The island is always in the same place, just off the Docks, and can't be moved in game. There are no hotkeys: walk there down a walkway from the Docks.
- Play without the mod and let the game save, and the game removes the island's data from that save (everything placed in the dome, its employees): the game does this to any property it can't find. Back up your save before removing the mod, and put the mod back before saving.
- If something doesn't work, post the lines containing "Greenhouse_Island" from MelonLoader/Latest.log in the comments.


CHANGELOG 1.1.0
----------------
- Works on Schedule I 0.4.7 (the current stable) as well as 0.4.6f13: the one game call that 0.4.7 removed (an NPC's walking agent) is now found in a way that works on both. Tested loading a save on 0.4.7f12: the island builds, its employees work, the tap, doors, stairs and weed plant are all there.
- Fixed a hitch of about 80 ms every 10 seconds in clear weather: the mod looked through the whole scene for the rain effects over and over. It now waits for the game to announce its rain.
- Fewer draw calls: about 1,200 small pieces of scenery (dome ribs, upstairs beams and rails, walkway planks) are merged into a few hundred meshes, and tiny pieces no longer cast shadows. It looks the same.
- Workers: an employee stuck in the plant's planter at the middle of the dome is set back on the floor (once per 10 seconds at most), and the plant and planter are no longer walkable for employees.
- At load the log (and a message on screen) now says if employees have no locker and how many pots have no harvest destination: those are the usual reasons a worker stands idle.
- Removed the "initialize order of 0" error the game logged for the island.
