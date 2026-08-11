extends SceneTree
func _initialize():
	var out := OS.get_environment("PCK_OUT")
	var packer := PCKPacker.new()
	var err := packer.pck_start(out)
	if err != OK:
		push_error("pck_start failed: %d" % err); quit(1); return
	packer.add_file("res://mod_manifest.json", ProjectSettings.globalize_path("res://mod_manifest.json"))
	err = packer.flush(true)
	if err != OK:
		push_error("flush failed: %d" % err); quit(1); return
	print("PCK written to: ", out)
	quit(0)
