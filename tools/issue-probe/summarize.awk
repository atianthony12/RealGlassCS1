# Summary of depth-<version>.txt: per case, the worst of the frames that are not the first at a size
# (frame 1; in "change", frame 4 too), and that first frame apart. rim = depth pixels where the void reads
# as something near or something near as the void (the line round a silhouette); depthDiffer counts any
# difference at all (ground and water reading a neighbour's depth too).
BEGIN { FS = "\t" }
$1 == "ROW" && $2 != "version" {
    key = $2 "\t" $3 "\t" $7 "\t" $5 "\t" $6 "\t" $4
    first = ($8 == 1 || ($5 == "change" && $8 == 4))
    if (!(key in seen)) { seen[key] = 1; order[++n] = key }
    if (first) { if ($11 > f1[key]) f1[key] = $11 }
    else {
        if ($11 > dd[key]) dd[key] = $11
        if ($12 + $13 > rim[key]) rim[key] = $12 + $13
        if ($15 > s16[key]) s16[key] = $15
        if ($16 > bl[key]) bl[key] = $16
    }
}
END {
    print "version\tview\tmask\tmode\teffects\tfactor\tdepthDiffer\tshownOver16\tbluer\tfirstFrameDepthDiffer\trim"
    for (i = 1; i <= n; i++) { k = order[i]; print k "\t" dd[k]+0 "\t" s16[k]+0 "\t" bl[k]+0 "\t" f1[k]+0 "\t" rim[k]+0 }
}
