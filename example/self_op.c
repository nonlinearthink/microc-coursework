void main()
{
    int i = 0;
    int j = ++i;
    print i;
    j--;
    print j;
    i = 0;
    j = i++;
    print i;
    --j;
    print j;
}