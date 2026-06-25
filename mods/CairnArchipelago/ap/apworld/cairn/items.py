from BaseClasses import Item, ItemClassification

from .data import GAME_ITEMS, ITEM_BASE

PROGRESSIVE_PERMIT = "Progressive Altitude Permit"
PERMIT_ID = ITEM_BASE - 1

CLASSIFICATIONS = {
    "useful": ItemClassification.useful,
    "filler": ItemClassification.filler,
}


class CairnItem(Item):
    game = "Cairn"


# Game items map 1:1 onto the game's InventoryItemStringIdEnum; the client mod
# recovers the enum value as (ap_item_id - ITEM_BASE).
item_name_to_id = {display: ITEM_BASE + value for display, _, value, _ in GAME_ITEMS}
item_name_to_id[PROGRESSIVE_PERMIT] = PERMIT_ID

item_classification = {
    display: CLASSIFICATIONS[category] for display, _, _, category in GAME_ITEMS
}
item_classification[PROGRESSIVE_PERMIT] = ItemClassification.progression

useful_item_names = [d for d, _, _, c in GAME_ITEMS if c == "useful"]
filler_item_names = [d for d, _, _, c in GAME_ITEMS if c == "filler"]

item_name_groups = {
    "Climbing Gear": set(useful_item_names),
}
